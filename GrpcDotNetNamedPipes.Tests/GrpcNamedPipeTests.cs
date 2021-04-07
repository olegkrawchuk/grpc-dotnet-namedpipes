/*
 * Copyright 2020 Google LLC
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * https://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using GrpcDotNetNamedPipes.Tests.Generated;
using GrpcDotNetNamedPipes.Tests.Helpers;
using Xunit;

namespace GrpcDotNetNamedPipes.Tests
{
    public class GrpcNamedPipeTests
    {
        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public void SimpleUnary(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var response = ctx.Client.SimpleUnary(new RequestMessage {Value = 10});
            Assert.Equal(10, response.Value);
            Assert.True(ctx.Impl.SimplyUnaryCalled);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task SimpleUnaryAsync(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var response = await ctx.Client.SimpleUnaryAsync(new RequestMessage {Value = 10});
            Assert.Equal(10, response.Value);
            Assert.True(ctx.Impl.SimplyUnaryCalled);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task LargePayload(ChannelContextFactory factory)
        {
            var bytes = new byte[1024 * 1024];
            new Random(1234).NextBytes(bytes);
            var byteString = ByteString.CopyFrom(bytes);

            using var ctx = factory.Create();
            var response = await ctx.Client.SimpleUnaryAsync(new RequestMessage {Binary = byteString});
            Assert.Equal(byteString, response.Binary);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task CancelUnary(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var cts = new CancellationTokenSource();
            var responseTask =
                ctx.Client.DelayedUnaryAsync(new RequestMessage {Value = 10}, cancellationToken: cts.Token);
            cts.CancelAfter(500);
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await responseTask);
            Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task CancelUnaryBeforeCall(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var responseTask =
                ctx.Client.SimpleUnaryAsync(new RequestMessage {Value = 10}, cancellationToken: cts.Token);
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await responseTask);
            Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
            Assert.False(ctx.Impl.SimplyUnaryCalled);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task ThrowingUnary(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var responseTask = ctx.Client.ThrowingUnaryAsync(new RequestMessage {Value = 10});
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await responseTask);
            Assert.Equal(StatusCode.Unknown, exception.StatusCode);
            Assert.Equal("Exception was thrown by handler.", exception.Status.Detail);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task ThrowCanceledExceptionUnary(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            ctx.Impl.ExceptionToThrow = new OperationCanceledException();
            var responseTask = ctx.Client.ThrowingUnaryAsync(new RequestMessage {Value = 10});
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await responseTask);
            Assert.Equal(StatusCode.Unknown, exception.StatusCode);
            Assert.Equal("Exception was thrown by handler.", exception.Status.Detail);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task ThrowRpcExceptionUnary(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            ctx.Impl.ExceptionToThrow = new RpcException(new Status(StatusCode.InvalidArgument, "Bad arg"));
            var responseTask = ctx.Client.ThrowingUnaryAsync(new RequestMessage {Value = 10});
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await responseTask);
            Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
            Assert.Equal("Bad arg", exception.Status.Detail);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task ThrowAfterCancelUnary(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var cts = new CancellationTokenSource();
            var responseTask =
                ctx.Client.DelayedThrowingUnaryAsync(new RequestMessage {Value = 10}, cancellationToken: cts.Token);
            cts.CancelAfter(500);
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await responseTask);
            Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task ClientStreaming(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var call = ctx.Client.ClientStreaming();
            await call.RequestStream.WriteAsync(new RequestMessage {Value = 3});
            await call.RequestStream.WriteAsync(new RequestMessage {Value = 2});
            await call.RequestStream.WriteAsync(new RequestMessage {Value = 1});
            await call.RequestStream.CompleteAsync();
            var response = await call.ResponseAsync;
            Assert.Equal(6, response.Value);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task CancelClientStreaming(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var cts = new CancellationTokenSource();
            var call = ctx.Client.ClientStreaming(cancellationToken: cts.Token);
            await call.RequestStream.WriteAsync(new RequestMessage {Value = 1});
            cts.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await call.RequestStream.WriteAsync(new RequestMessage {Value = 1}));
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
            Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task CancelClientStreamingBeforeCall(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var call = ctx.Client.ClientStreaming(cancellationToken: cts.Token);
            await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await call.RequestStream.WriteAsync(new RequestMessage {Value = 1}));
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
            Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task ServerStreaming(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var call = ctx.Client.ServerStreaming(new RequestMessage {Value = 3});
            Assert.True(await call.ResponseStream.MoveNext());
            Assert.Equal(3, call.ResponseStream.Current.Value);
            Assert.True(await call.ResponseStream.MoveNext());
            Assert.Equal(2, call.ResponseStream.Current.Value);
            Assert.True(await call.ResponseStream.MoveNext());
            Assert.Equal(1, call.ResponseStream.Current.Value);
            Assert.False(await call.ResponseStream.MoveNext());
        }

        [Theory(Skip = "Flaky")]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task CancelServerStreaming(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var cts = new CancellationTokenSource();
            var call = ctx.Client.DelayedServerStreaming(new RequestMessage {Value = 3},
                cancellationToken: cts.Token);
            cts.CancelAfter(500);
            Assert.True(await call.ResponseStream.MoveNext());
            Assert.Equal(3, call.ResponseStream.Current.Value);
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());
            Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task CancelServerStreamingBeforeCall(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var call = ctx.Client.DelayedServerStreaming(new RequestMessage {Value = 3},
                cancellationToken: cts.Token);
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());
            Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task ThrowingServerStreaming(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var call = ctx.Client.ThrowingServerStreaming(new RequestMessage { Value = 1 });
            Assert.True(await call.ResponseStream.MoveNext());
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());
            Assert.Equal(StatusCode.Unknown, exception.StatusCode);
            Assert.Equal("Exception was thrown by handler.", exception.Status.Detail);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task DuplexStreaming(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var call = ctx.Client.DuplexStreaming();

            Assert.True(await call.ResponseStream.MoveNext());
            Assert.Equal(10, call.ResponseStream.Current.Value);
            Assert.True(await call.ResponseStream.MoveNext());
            Assert.Equal(11, call.ResponseStream.Current.Value);

            await call.RequestStream.WriteAsync(new RequestMessage {Value = 3});
            await call.RequestStream.WriteAsync(new RequestMessage {Value = 2});
            Assert.True(await call.ResponseStream.MoveNext());
            Assert.Equal(3, call.ResponseStream.Current.Value);
            Assert.True(await call.ResponseStream.MoveNext());
            Assert.Equal(2, call.ResponseStream.Current.Value);

            await call.RequestStream.WriteAsync(new RequestMessage {Value = 1});
            await call.RequestStream.CompleteAsync();
            Assert.True(await call.ResponseStream.MoveNext());
            Assert.Equal(1, call.ResponseStream.Current.Value);
            Assert.False(await call.ResponseStream.MoveNext());
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task CancelDuplexStreaming(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var cts = new CancellationTokenSource();
            var call = ctx.Client.DelayedDuplexStreaming(cancellationToken: cts.Token);
            cts.CancelAfter(500);
            await call.RequestStream.WriteAsync(new RequestMessage {Value = 1});
            Assert.True(await call.ResponseStream.MoveNext());
            Assert.Equal(1, call.ResponseStream.Current.Value);
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());
            Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task CancelDuplexStreamingBeforeCall(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var call = ctx.Client.DelayedDuplexStreaming(cancellationToken: cts.Token);
            await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await call.RequestStream.WriteAsync(new RequestMessage {Value = 1}));
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());
            Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task ThrowingDuplexStreaming(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var call = ctx.Client.ThrowingDuplexStreaming();
            await call.RequestStream.WriteAsync(new RequestMessage {Value = 1});
            await call.RequestStream.CompleteAsync();
            Assert.True(await call.ResponseStream.MoveNext());
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());
            Assert.Equal(StatusCode.Unknown, exception.StatusCode);
            Assert.Equal("Exception was thrown by handler.", exception.Status.Detail);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task SetStatus(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var call = ctx.Client.SetStatusAsync(new RequestMessage());
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call);
            Assert.Equal(StatusCode.InvalidArgument, exception.Status.StatusCode);
            Assert.Equal("invalid argument", exception.Status.Detail);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task Deadline(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(0.1);
            var call = ctx.Client.DelayedUnaryAsync(new RequestMessage(), deadline: deadline);
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call);
            Assert.Equal(StatusCode.DeadlineExceeded, exception.StatusCode);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task AlreadyExpiredDeadline(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var deadline = DateTime.UtcNow - TimeSpan.FromSeconds(0.1);
            var call = ctx.Client.SimpleUnaryAsync(new RequestMessage(), deadline: deadline);
            var exception = await Assert.ThrowsAsync<RpcException>(async () => await call);
            Assert.Equal(StatusCode.DeadlineExceeded, exception.StatusCode);
            Assert.False(ctx.Impl.SimplyUnaryCalled);
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public async Task HeadersAndTrailers(ChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var requestHeaders = new Metadata
            {
                {"A1", "1"},
                {"A2-bin", new[] {(byte) 2}},
            };
            var responseHeaders = new Metadata
            {
                {"B1", "1"},
                {"B2-bin", new[] {(byte) 2}},
            };
            var responseTrailers = new Metadata
            {
                {"C1", "1"},
                {"C2-bin", new[] {(byte) 2}},
            };

            ctx.Impl.ResponseHeaders = responseHeaders;
            ctx.Impl.ResponseTrailers = responseTrailers;
            var call = ctx.Client.HeadersTrailersAsync(new RequestMessage {Value = 1}, requestHeaders);

            var actualResponseHeaders = await call.ResponseHeadersAsync;
            await call.ResponseAsync;
            var actualResponseTrailers = call.GetTrailers();
            var actualStatus = call.GetStatus();
            var actualRequestHeaders = ctx.Impl.RequestHeaders;

            AssertHasMetadata(requestHeaders, actualRequestHeaders);
            AssertHasMetadata(responseHeaders, actualResponseHeaders);
            AssertHasMetadata(responseTrailers, actualResponseTrailers);
            Assert.Equal(StatusCode.OK, actualStatus.StatusCode);
        }

        private void AssertHasMetadata(Metadata expected, Metadata actual)
        {
            var actualDict = actual.ToDictionary(x => x.Key);
            foreach (var expectedEntry in expected)
            {
                Assert.True(actualDict.ContainsKey(expectedEntry.Key));
                var actualEntry = actualDict[expectedEntry.Key];
                Assert.Equal(expectedEntry.IsBinary, actualEntry.IsBinary);
                if (expectedEntry.IsBinary)
                {
                    Assert.Equal(expectedEntry.ValueBytes.AsEnumerable(), actualEntry.ValueBytes.AsEnumerable());
                }
                else
                {
                    Assert.Equal(expectedEntry.Value, actualEntry.Value);
                }
            }
        }

        [Theory]
        [ClassData(typeof(MultiChannelClassData))]
        public void ConnectionTimeout(ChannelContextFactory factory)
        {
            var client = factory.CreateClient();
            var exception = Assert.Throws<RpcException>(() => client.SimpleUnary(new RequestMessage { Value = 10 }));
            Assert.Equal(StatusCode.Unavailable, exception.StatusCode);
            Assert.Equal("failed to connect to all addresses", exception.Status.Detail);
        }

        [Theory]
        [ClassData(typeof(NamedPipeClassData))]
        public async Task CancellationRace(NamedPipeChannelContextFactory factory)
        {
            using var ctx = factory.Create();
            var random = new Random();
            for (int i = 0; i < 200; i++)
            {
                var cts = new CancellationTokenSource();
                var response = ctx.Client.SimpleUnaryAsync(new RequestMessage { Value = 10 }, cancellationToken: cts.Token);
                Thread.Sleep(random.Next(10));
                cts.Cancel();
                // Either a result or cancellation is okay, but we shouldn't get any other errors
                try
                {
                    Assert.Equal(10, (await response).Value);
                }
                catch (RpcException ex)
                {
                    Assert.Equal(StatusCode.Cancelled, ex.StatusCode);
                }
            }
        }

#if NET_5_0 || NETFRAMEWORK
        [Theory]
        [ClassData(typeof(NamedPipeClassData))]
        public void SimpleUnaryWithACLs(NamedPipeChannelContextFactory factory)
        {
            PipeSecurity security = new PipeSecurity();
            SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));

            NamedPipeServerOptions options = new NamedPipeServerOptions { PipeSecurity = security };

            using var ctx = factory.Create(options);
            var response = ctx.Client.SimpleUnary(new RequestMessage { Value = 10 });
            Assert.Equal(10, response.Value);
            Assert.True(ctx.Impl.SimplyUnaryCalled);
        }

        [Theory]
        [ClassData(typeof(NamedPipeClassData))]
        public void SimpleUnaryWithACLsDenied(NamedPipeChannelContextFactory factory)
        {
            PipeSecurity security = new PipeSecurity();
            SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.ReadWrite, AccessControlType.Deny));

            NamedPipeServerOptions options = new NamedPipeServerOptions { PipeSecurity = security };

            using var ctx = factory.Create(options);
            var exception = Assert.Throws<UnauthorizedAccessException>(() => ctx.Client.SimpleUnary(new RequestMessage { Value = 10 }));
        }
#endif
    }
}