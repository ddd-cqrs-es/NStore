﻿using BenchmarkDotNet.Running;

namespace NStore.Benchmarks
{
    public class Program
    {
        public static void Main(string[] args)
        {
            //BenchmarkRunner.Run<MongoBatchWriteBenchmark>();
            //BenchmarkRunner.Run<MongoTplAppendAsyncBenchmark>();
            BenchmarkRunner.Run<SqliteBatchWriteBenchmark>();
        }
    }
}
