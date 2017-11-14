using System;
using System.Text;
using NStore.Core.Logging;

namespace NStore.Persistence.MsSql
{
    public class MsSqlPersistenceOptions
    {
        public INStoreLoggerFactory LoggerFactory { get; set; }
        public IMsSqlPayloadSearializer Serializer { get; set; }
        public string ConnectionString { get; set; }
        public string StreamsTableName { get; set; }

        public MsSqlPersistenceOptions(INStoreLoggerFactory loggerFactory)
        {
            LoggerFactory = loggerFactory;
            StreamsTableName = "Streams";
        }

        public virtual string GetCreateTableScript()
        {
            return $@"CREATE TABLE [{StreamsTableName}](
                [Position] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                [PartitionId] NVARCHAR(255) NOT NULL,
                [OperationId] NVARCHAR(255) NOT NULL,
                [SerializerInfo] NVARCHAR(255) NOT NULL,
                [Index] BIGINT NOT NULL,
                [Payload] VARBINARY(MAX)
            )

            CREATE UNIQUE INDEX IX_{StreamsTableName}_OPID on dbo.{StreamsTableName} (PartitionId, OperationId)
            CREATE UNIQUE INDEX IX_{StreamsTableName}_IDX on dbo.{StreamsTableName} (PartitionId, [Index])
";
        }

        public virtual string GetPersistScript()
        {
            return $@"INSERT INTO [{StreamsTableName}]
                      ([PartitionId], [Index], [Payload], [OperationId], [SerializerInfo])
                      OUTPUT INSERTED.[Position] 
                      VALUES (@PartitionId, @Index, @Payload, @OperationId, @SerializerInfo)";
        }

        public virtual string GetDeleteStreamScript()
        {
            return $@"DELETE FROM [{StreamsTableName}] WHERE 
                          [PartitionId] = @PartitionId 
                      AND [Index] BETWEEN @fromLowerIndexInclusive AND @toUpperIndexInclusive";
        }

        public virtual string GetFindByStreamAndOperation()
        {
            return $@"SELECT [Position], [PartitionId], [Index], [Payload], [OperationId], [SerializerInfo]
                      FROM [{StreamsTableName}] 
                      WHERE [PartitionId] = @PartitionId AND [OperationId] = @OperationId";
        }

        public virtual string GetFindAllByOperation()
        {
            return $@"SELECT [Position], [PartitionId], [Index], [Payload], [OperationId], [SerializerInfo]
                      FROM [{StreamsTableName}] 
                      WHERE [OperationId] = @OperationId";
        }

        public virtual string GetLastChunkScript()
        {
            return $@"SELECT TOP 1 
                        [Position], [PartitionId], [Index], [Payload], [OperationId], [SerializerInfo]
                      FROM 
                        [{StreamsTableName}] 
                      WHERE 
                          [PartitionId] = @PartitionId 
                      AND [Index] <= @toUpperIndexInclusive 
                      ORDER BY 
                          [Position] DESC";
        }

        public virtual string BuildSelect(
            long lowerIndexInclusive,
            long upperIndexInclusive,
            int limit
        )
        {
            return BuildSelect2(upperIndexInclusive, lowerIndexInclusive, limit, false);
        }

        public string BuildSelect2(
            long upperIndexInclusive,
            long lowerIndexInclusive,
            int limit,
            bool descending = true
        )
        {
            var sb = new StringBuilder("SELECT ");
            if (limit > 0 && limit != int.MaxValue)
            {
                sb.Append($"TOP {limit} ");
            }

            sb.Append("[Position], [PartitionId], [Index], [Payload], [OperationId], [SerializerInfo] ");
            sb.Append($"FROM {StreamsTableName} ");
            sb.Append($"WHERE [PartitionId] = @PartitionId ");

            if (lowerIndexInclusive > 0 && lowerIndexInclusive != Int64.MinValue)
            {
                sb.Append("AND [Index] >= @lowerIndexInclusive ");
            }

            if (upperIndexInclusive > 0 && upperIndexInclusive != Int64.MaxValue)
            {
                sb.Append("AND [Index] <= @upperIndexInclusive ");
            }

            sb.Append(@descending ? "ORDER BY [Index] DESC" : "ORDER BY [Index]");

            return sb.ToString();
        }
    }
}