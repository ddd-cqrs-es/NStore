﻿using System;
using System.Threading.Tasks;
using NStore.Core.Logging;
using NStore.Core.Persistence;
using NStore.Core.Snapshots;
using NStore.Core.Streams;

namespace NStore.Domain.Experimental
{
    public class DomainRuntime
    {
        private readonly IPersistence _persistence;
        private readonly IAggregateFactory _aggregateFactory;
        private readonly IStreamsFactory _streamsFactory;
        private readonly ISnapshotStore _snapshots;
        private readonly PollingClient _pollingClient;

        public DomainRuntime(
            IPersistence persistence,
            IAggregateFactory aggregateFactory,
            ISnapshotStore snapshots,
            ChunkProcessor processor)
        {
            _persistence = persistence;
            _aggregateFactory = aggregateFactory;
            _snapshots = snapshots;
            _streamsFactory = new StreamsFactory(persistence);

            if (processor != null)
            {
                _pollingClient = new PollingClient(
                    persistence, 
                    0, // <----- TODO: read from state?
                    new LambdaSubscription(processor),
                    NStoreNullLoggerFactory.Instance
                );
                _pollingClient.Start();
            }
        }

        public Task MutateAsync<TAggregate>(string aggreagateId, Action<TAggregate> action)
            where TAggregate : IAggregate
        {
            return MutateAsync(aggreagateId, Guid.NewGuid().ToString(), action);
        }

        public async Task MutateAsync<TAggregate>(string aggreagateId, string operationId, Action<TAggregate> action)
            where TAggregate : IAggregate
        {
            var repo = new Repository(_aggregateFactory, _streamsFactory, _snapshots);
            var aggregate = await repo.GetByIdAsync<TAggregate>(aggreagateId).ConfigureAwait(false);
            action(aggregate);
            await repo.SaveAsync(aggregate, operationId);
        }

        public Recording Record(string sessionId)
        {
            return new Recording(sessionId, new Repository(_aggregateFactory, _streamsFactory, _snapshots));
        }

        public IReadOnlyStream OpenForRead(string streamId)
        {
            return _streamsFactory.OpenReadOnly(streamId);
        }

        public async Task CatchUpAsync()
        {
            if (_pollingClient == null) return;

            while (true)
            {
                long maxPos = await _persistence.ReadLastPositionAsync().ConfigureAwait(false);
                if (maxPos > _pollingClient.Position)
                {
                    await Task.Delay(500);
                    continue;
                }
                break;
            }
        }

        public async Task ShutdownAsync()
        {
            if (_pollingClient != null)
            {
                await _pollingClient.Stop().ConfigureAwait(false);
            }
        }

        public Task PushAsync(string views, object payload)
        {
            return _streamsFactory.Open(views).AppendAsync(payload);
        }
    }
}