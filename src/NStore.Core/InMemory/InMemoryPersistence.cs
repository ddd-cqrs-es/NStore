﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NStore.Core.Persistence;

namespace NStore.Core.InMemory
{
    public class InMemoryPersistence : IPersistence
    {
        private readonly Func<object, object> _cloneFunc;
        private readonly Chunk[] _chunks;

        private readonly ConcurrentDictionary<string, InMemoryPartition> _partitions =
            new ConcurrentDictionary<string, InMemoryPartition>();

        private int _sequence = 0;
        private int _lastWrittenPosition = -1;
        private readonly INetworkSimulator _networkSimulator;
        private readonly InMemoryPartition _emptyInMemoryPartition;
        private readonly ReaderWriterLockSlim _lockSlim = new ReaderWriterLockSlim();

        public bool SupportsFillers => true;

        public InMemoryPersistence() : this(null, null)
        {
        }

        public InMemoryPersistence(INetworkSimulator networkSimulator)
            : this(networkSimulator, null)
        {
        }

        public InMemoryPersistence(Func<object, object> cloneFunc)
            : this(null, cloneFunc)
        {
        }

        public InMemoryPersistence(INetworkSimulator networkSimulator, Func<object, object> cloneFunc)
        {
            _chunks = new Chunk[1024 * 1024];
            _cloneFunc = cloneFunc ?? (o => o);
            _networkSimulator = networkSimulator ?? new NoNetworkLatencySimulator();
            _emptyInMemoryPartition = new InMemoryPartition("::empty", _networkSimulator, Clone);
            _partitions.TryAdd(_emptyInMemoryPartition.Id, _emptyInMemoryPartition);
        }

        public Task ReadForwardAsync(
            string partitionId,
            long fromLowerIndexInclusive,
            ISubscription subscription,
            long toUpperIndexInclusive,
            int limit,
            CancellationToken cancellationToken
        )
        {
            if (!_partitions.TryGetValue(partitionId, out var partition))
            {
                return Task.CompletedTask;
            }

            return partition.ReadForward(
                fromLowerIndexInclusive,
                subscription,
                toUpperIndexInclusive,
                limit,
                cancellationToken
            );
        }

        public Task ReadBackwardAsync(
            string partitionId,
            long fromUpperIndexInclusive,
            ISubscription subscription,
            long toLowerIndexInclusive,
            int limit,
            CancellationToken cancellationToken
        )
        {
            if (!_partitions.TryGetValue(partitionId, out var partition))
            {
                return Task.CompletedTask;
            }

            return partition.ReadBackward(
                fromUpperIndexInclusive,
                subscription,
                toLowerIndexInclusive,
                limit,
                cancellationToken
            );
        }

        public Task<IChunk> ReadSingleBackwardAsync(string partitionId, long fromUpperIndexInclusive, CancellationToken cancellationToken)
        {
            if (!_partitions.TryGetValue(partitionId, out var partition))
            {
                return Task.FromResult<IChunk>(null);
            }

            return partition.Peek(fromUpperIndexInclusive, cancellationToken);
        }

        private Chunk Clone(Chunk source)
        {
            if (source == null)
                return null;

            return new Chunk()
            {
                Position = source.Position,
                Index = source.Index,
                OperationId = source.OperationId,
                PartitionId = source.PartitionId,
                Payload = _cloneFunc(source.Payload)
            };
        }

        public async Task ReadAllAsync(long fromPositionInclusive, ISubscription subscription, int limit, CancellationToken cancellationToken)
        {
            await subscription.OnStartAsync(fromPositionInclusive).ConfigureAwait(false);

            int start = (int)Math.Max(fromPositionInclusive - 1, 0);

            _lockSlim.EnterReadLock();
            int lastWritten = _lastWrittenPosition;
            _lockSlim.ExitReadLock();

            if (start > lastWritten)
            {
                await subscription.StoppedAsync(fromPositionInclusive).ConfigureAwait(false);
                return;
            }

            var toRead = Math.Min(limit, lastWritten - start + 1);
            if (toRead <= 0)
            {
                await subscription.StoppedAsync(fromPositionInclusive).ConfigureAwait(false);
                return;
            }

            IEnumerable<Chunk> list = new ArraySegment<Chunk>(_chunks, start, toRead);

            long position = 0;

            try
            {
                foreach (var chunk in list)
                {
                    if (chunk.Deleted)
                    {
                        continue;
                    }

                    position = chunk.Position;

                    await _networkSimulator.Wait().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!await subscription.OnNextAsync(Clone(chunk)).ConfigureAwait(false))
                    {
                        await subscription.StoppedAsync(position).ConfigureAwait(false);
                        return;
                    }
                }

                if (position == 0)
                {
                    await subscription.StoppedAsync(fromPositionInclusive).ConfigureAwait(false);
                }
                else
                {
                    await subscription.CompletedAsync(position).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                await subscription.OnErrorAsync(position, e).ConfigureAwait(false);
            }
        }

        public Task<long> ReadLastPositionAsync(CancellationToken cancellationToken)
        {
            try
            {
                _lockSlim.EnterReadLock();
                if (_lastWrittenPosition == -1)
                    return Task.FromResult(0L);

                return Task.FromResult(_chunks[_lastWrittenPosition].Position);
            }
            finally
            {
                _lockSlim.ExitReadLock();
            }
        }

        public async Task<IChunk> AppendAsync(string partitionId, long index, object payload, string operationId, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _sequence);
            var chunk = new Chunk()
            {
                Position = id,
                Index = index >= 0 ? index : id,
                OperationId = operationId ?? Guid.NewGuid().ToString(),
                PartitionId = partitionId,
                Payload = _cloneFunc(payload)
            };

            await _networkSimulator.Wait().ConfigureAwait(false);

            var partion = _partitions.GetOrAdd(partitionId,
                new InMemoryPartition(partitionId, _networkSimulator, Clone)
            );

            try
            {
                var chunkWritten = partion.Write(chunk);
                if (!chunkWritten)
                {
                    //idempotency on operationId.
                    return null;
                }
            }
            catch (DuplicateStreamIndexException)
            {
                // write empty chunk
                // keep same id to avoid holes in the stream
                chunk.PartitionId = "::empty";
                chunk.Index = chunk.Position;
                chunk.OperationId = chunk.Position.ToString();
                chunk.Payload = null;
                _emptyInMemoryPartition.Write(chunk);
                SetChunk(chunk);
                throw;
            }
            SetChunk(chunk);
            await _networkSimulator.Wait().ConfigureAwait(false);

            return chunk;
        }

        private void SetChunk(Chunk chunk)
        {
            int slot = (int)chunk.Position - 1;
            _chunks[slot] = chunk;

            _lockSlim.EnterWriteLock();
            if (_lastWrittenPosition < slot)
            {
                _lastWrittenPosition = slot;
            }
            _lockSlim.ExitWriteLock();
        }

        public async Task DeleteAsync(
            string partitionId,
            long fromLowerIndexInclusive,
            long toUpperIndexInclusive,
            CancellationToken cancellationToken
        )
        {
            await _networkSimulator.Wait().ConfigureAwait(false);

            if (!_partitions.TryGetValue(partitionId, out var partition))
            {
                throw new StreamDeleteException(partitionId);
            }

            var deleted = partition.Delete(fromLowerIndexInclusive, toUpperIndexInclusive);
            if (deleted.Length == 0)
            {
                throw new StreamDeleteException(partitionId);
            }

            foreach (var d in deleted)
            {
                d.Deleted = true;
            }
        }

        public async Task<IChunk> ReadByOperationIdAsync(
            string partitionId,
            string operationId,
            CancellationToken cancellationToken)
        {
            await _networkSimulator.Wait().ConfigureAwait(false);

            if (!_partitions.TryGetValue(partitionId, out var partition))
            {
                return null;
            }

            return await partition.GetByOperationId(operationId).ConfigureAwait(false);
        }

        public async Task ReadAllByOperationIdAsync(
            string operationId,
            ISubscription subscription,
            CancellationToken cancellationToken)
        {
            await _networkSimulator.Wait().ConfigureAwait(false);
            var filter = new SubscriptionWrapper(subscription)
            {
                ChunkFilter = chunk => chunk.OperationId == operationId
            };

            await ReadAllAsync(0, filter, int.MaxValue, cancellationToken).ConfigureAwait(false);
        }
    }
}