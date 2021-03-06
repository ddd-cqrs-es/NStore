﻿using NStore.Core.InMemory;
using NStore.Core.Persistence;
using NStore.Core.Snapshots;
using NStore.Persistence;

namespace NStore.Persistence.Tests
{
    public partial class BasePersistenceTest
    {
        private const string TestSuitePrefix = "Memory";

        private IPersistence Create()
        {
            var store = new InMemoryPersistence(cloneFunc:Clone);
            return store;
        }

        private void Clear()
        {
        }
        
        private static object Clone(object source)
        {
            if (source == null)
                return null;

            if (source is SnapshotInfo si)
            {
                return new SnapshotInfo(
                    si.SourceId,
                    si.SourceVersion,
                    new State((State) si.Payload),
                    si.SchemaVersion
                );
            }

            return source;
        }
    }
}