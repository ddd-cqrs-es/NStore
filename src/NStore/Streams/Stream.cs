﻿using System;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using NStore.Persistence;

namespace NStore.Streams
{
    public class Stream : IStream
    {
        private IPersistence Persistence { get; }
        public string Id { get; }
        public virtual bool IsWritable => true;
        public Stream(string streamId, IPersistence persistence)
        {
            this.Id = streamId;
            this.Persistence = persistence;
        }

        public Task Read(IPartitionConsumer partitionConsumer, int fromIndexInclusive, int toIndexInclusive, CancellationToken cancellationToken)
        {
            return Persistence.ReadPartitionForward(
                Id,
                fromIndexInclusive,
                partitionConsumer,
                toIndexInclusive,
                int.MaxValue,
                cancellationToken
            );
        }

        public virtual Task Append(object payload, string operationId, CancellationToken cancellation)
        {
            return Persistence.PersistAsync(this.Id, -1, payload, operationId, cancellation);
        }

        public virtual Task Delete(CancellationToken cancellation)
        {
            return Persistence.DeleteAsync(this.Id, 0, long.MaxValue, cancellation);
        }
    }
}