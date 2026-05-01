using Grpc.Core;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.Test {
    internal sealed class TestServerCallContext : ServerCallContext {
        private readonly CancellationToken cancellationToken;
        private readonly Metadata responseTrailers = new Metadata();
        private readonly IDictionary<object, object> userState = new Dictionary<object, object>();
        private Status status;
        private WriteOptions writeOptions;

        private TestServerCallContext(CancellationToken cancellationToken) {
            this.cancellationToken = cancellationToken;
        }

        public static TestServerCallContext Create(CancellationToken cancellationToken = default) {
            return new TestServerCallContext(cancellationToken);
        }

        protected override string MethodCore => "test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
        protected override Metadata RequestHeadersCore => new Metadata();
        protected override CancellationToken CancellationTokenCore => cancellationToken;
        protected override Metadata ResponseTrailersCore => responseTrailers;

        protected override Status StatusCore {
            get => status;
            set => status = value;
        }

        protected override WriteOptions WriteOptionsCore {
            get => writeOptions;
            set => writeOptions = value;
        }

        protected override AuthContext AuthContextCore => new AuthContext(string.Empty, new Dictionary<string, List<AuthProperty>>());
        protected override IDictionary<object, object> UserStateCore => userState;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) {
            throw new NotSupportedException();
        }

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) {
            return Task.CompletedTask;
        }
    }
}
