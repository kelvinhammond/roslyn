﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.UnitTests
{
    public class TemporaryStorageServiceTests
    {
        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestTemporaryStorageText()
        {
            var textFactory = new TextFactoryServiceFactory.TextFactoryService();
            var service = new TemporaryStorageServiceFactory.TemporaryStorageService(textFactory);

            // test normal string
            var text = SourceText.From(new string(' ', 4096) + "public class A {}");
            TestTemporaryStorage(service, text);

            // test empty string
            text = SourceText.From(string.Empty);
            TestTemporaryStorage(service, text);

            // test large string
            text = SourceText.From(new string(' ', 1024 * 1024) + "public class A {}");
            TestTemporaryStorage(service, text);
        }

        [WorkItem(531188)]
        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestTemporaryStorageStream()
        {
            var textFactory = new TextFactoryServiceFactory.TextFactoryService();
            var service = new TemporaryStorageServiceFactory.TemporaryStorageService(textFactory);
            var temporaryStorage = service.CreateTemporaryStorage(System.Threading.CancellationToken.None);

            using (var data = SerializableBytes.CreateWritableStream())
            {
                for (int i = 0; i < SharedPools.ByteBufferSize; i++)
                {
                    data.WriteByte((byte)(i % 2));
                }

                data.Position = 0;
                temporaryStorage.WriteStreamAsync(data).Wait();
                using (var result = temporaryStorage.ReadStreamAsync().Result)
                {
                    Assert.Equal(data.Length, result.Length);

                    for (int i = 0; i < SharedPools.ByteBufferSize; i++)
                    {
                        Assert.Equal(i % 2, result.ReadByte());
                    }
                }
            }
        }

        private void TestTemporaryStorage(ITemporaryStorageService temporaryStorageService, SourceText text)
        {
            // create a temporary storage location
            var temporaryStorage = temporaryStorageService.CreateTemporaryStorage(System.Threading.CancellationToken.None);

            // write text into it
            temporaryStorage.WriteTextAsync(text).Wait();

            // read text back from it
            var text2 = temporaryStorage.ReadTextAsync().Result;

            Assert.NotSame(text, text2);
            Assert.Equal(text.ToString(), text2.ToString());

            temporaryStorage.Dispose();
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestTemporaryStorageExceptions()
        {
            var textFactory = new TextFactoryServiceFactory.TextFactoryService();
            var service = new TemporaryStorageServiceFactory.TemporaryStorageService(textFactory);
            var storage = service.CreateTemporaryStorage(CancellationToken.None);

            // Nothing has been written yet
            Assert.Throws<InvalidOperationException>(() => storage.ReadText());
            Assert.Throws<AggregateException>(() => storage.ReadTextAsync().Result);
            Assert.Throws<InvalidOperationException>(() => storage.ReadStream());
            Assert.Throws<AggregateException>(() => storage.ReadStreamAsync().Result);

            // 0 length streams are not allowed
            var stream = new MemoryStream();
            Assert.Throws<ArgumentOutOfRangeException>(() => storage.WriteStream(stream));
            Assert.Throws<AggregateException>(() => storage.WriteStreamAsync(stream).Wait());

            // write a normal string
            var text = SourceText.From(new string(' ', 4096) + "public class A {}");
            storage.WriteTextAsync(text).Wait();

            // Writing multiple times is not allowed
            Assert.Throws<InvalidOperationException>(() => storage.WriteText(text));
            Assert.Throws<AggregateException>(() => storage.WriteTextAsync(text).Wait());
            Assert.Throws<InvalidOperationException>(() => storage.WriteStream(null));
            Assert.Throws<AggregateException>(() => storage.WriteStreamAsync(null).Wait());
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestTemporaryStorageMemoryMappedFileManagement()
        {
            var textFactory = new TextFactoryServiceFactory.TextFactoryService();
            var service = new TemporaryStorageServiceFactory.TemporaryStorageService(textFactory);
            var buffer = new MemoryStream(257 * 1024 + 1);
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer.WriteByte((byte)i);
            }

            // Do a relatively cheap concurrent stress test of the backing MemoryMappedFile management
            var tasks = Enumerable.Range(1, 257).Select(async i =>
            {
                for (int j = 1; j < 5; j++)
                {
                    using (ITemporaryStorage storage1 = service.CreateTemporaryStorage(CancellationToken.None),
                        storage2 = service.CreateTemporaryStorage(CancellationToken.None))
                    {
                        var storage3 = service.CreateTemporaryStorage(CancellationToken.None); // let the finalizer run for this instance

                        storage1.WriteStream(new MemoryStream(buffer.GetBuffer(), 0, 1024 * i - 1));
                        storage2.WriteStream(new MemoryStream(buffer.GetBuffer(), 0, 1024 * i));
                        storage3.WriteStream(new MemoryStream(buffer.GetBuffer(), 0, 1024 * i + 1));

                        await Task.Yield();

                        using (Stream s1 = storage1.ReadStream(),
                            s2 = storage2.ReadStream(),
                            s3 = storage3.ReadStream())
                        {
                            Assert.Equal(1024 * i - 1, s1.Length);
                            Assert.Equal(1024 * i, s2.Length);
                            Assert.Equal(1024 * i + 1, s3.Length);
                        }
                    }
                }
            });

            Task.WaitAll(tasks.ToArray());
            GC.Collect(2);
            GC.WaitForPendingFinalizers();
            GC.Collect(2);
        }

        // We want to keep this test around, but not have it disabled/associated with a bug
        // [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestTemporaryStorageScaling()
        {
            // This will churn through 4GB of memory.  It validates that we don't
            // use up our address space in a 32 bit process.
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess)
            {
                var textFactory = new TextFactoryServiceFactory.TextFactoryService();
                var service = new TemporaryStorageServiceFactory.TemporaryStorageService(textFactory);

                using (var data = SerializableBytes.CreateWritableStream())
                {
                    for (int i = 0; i < 1024 * 128; i++)
                    {
                        data.WriteByte(1);
                    }

                    // Create 4GB of memory mapped files
                    int fileCount = (int)((long)4 * 1024 * 1024 * 1024 / data.Length);
                    var storageHandles = new List<ITemporaryStorage>(fileCount);
                    for (int i = 0; i < fileCount; i++)
                    {
                        var s = service.CreateTemporaryStorage(CancellationToken.None);
                        storageHandles.Add(s);
                        data.Position = 0;
                        s.WriteStreamAsync(data).Wait();
                    }

                    for (int i = 0; i < 1024 * 5; i++)
                    {
                        using (var s = storageHandles[i].ReadStreamAsync().Result)
                        {
                            Assert.Equal(1, s.ReadByte());
                            storageHandles[i].Dispose();
                        }
                    }
                }
            }
        }

        [Fact]
        public void StreamTest1()
        {
            var textFactory = new TextFactoryServiceFactory.TextFactoryService();
            var service = new TemporaryStorageServiceFactory.TemporaryStorageService(textFactory);
            var storage = service.CreateTemporaryStorage(CancellationToken.None);

            using (var expected = new MemoryStream())
            {
                for (var i = 0; i < 10000; i++)
                {
                    expected.WriteByte((byte)(i % byte.MaxValue));
                }

                expected.Position = 0;
                storage.WriteStream(expected);

                expected.Position = 0;
                using (var stream = storage.ReadStream())
                {
                    Assert.Equal(expected.Length, stream.Length);

                    for (var i = 0; i < expected.Length; i++)
                    {
                        Assert.Equal(expected.ReadByte(), stream.ReadByte());
                    }
                }
            }
        }

        [Fact]
        public void StreamTest2()
        {
            var textFactory = new TextFactoryServiceFactory.TextFactoryService();
            var service = new TemporaryStorageServiceFactory.TemporaryStorageService(textFactory);
            var storage = service.CreateTemporaryStorage(CancellationToken.None);

            using (var expected = new MemoryStream())
            {
                for (var i = 0; i < 10000; i++)
                {
                    expected.WriteByte((byte)(i % byte.MaxValue));
                }

                expected.Position = 0;
                storage.WriteStream(expected);

                expected.Position = 0;
                using (var stream = storage.ReadStream())
                {
                    Assert.Equal(expected.Length, stream.Length);

                    int index = 0;
                    int count;
                    var bytes = new byte[1000];

                    while ((count = stream.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        for (var i = 0; i < count; i++)
                        {
                            Assert.Equal((byte)(index % byte.MaxValue), bytes[i]);
                            index++;
                        }
                    }

                    Assert.Equal(index, stream.Length);
                }
            }
        }

        [Fact]
        public void StreamTest3()
        {
            var textFactory = new TextFactoryServiceFactory.TextFactoryService();
            var service = new TemporaryStorageServiceFactory.TemporaryStorageService(textFactory);
            var storage = service.CreateTemporaryStorage(CancellationToken.None);

            using (var expected = new MemoryStream())
            {
                var random = new Random(Environment.TickCount);
                for (var i = 0; i < 100; i++)
                {
                    var position = random.Next(10000);
                    expected.Position = position;

                    var value = (byte)(i % byte.MaxValue);
                    expected.WriteByte(value);
                }

                expected.Position = 0;
                storage.WriteStream(expected);

                expected.Position = 0;
                using (var stream = storage.ReadStream())
                {
                    Assert.Equal(expected.Length, stream.Length);

                    for (int i = 0; i < expected.Length; i++)
                    {
                        var value = expected.ReadByte();
                        if (value != 0)
                        {
                            stream.Position = i;
                            Assert.Equal(value, stream.ReadByte());
                        }
                    }
                }
            }
        }
    }
}
