﻿using Cassandra;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GraphView.Transaction
{
    internal partial class PartitionedCassandraVersionTable : VersionTable
    {
        internal int PartitionCount { get; private set; }

        internal CassandraSessionManager SessionManager
        {
            get
            {
                return (this.VersionDb as PartitionedCassandraVersionDb).SessionManager;
            }
        }

        internal RequestQueue<VersionEntryRequest>[] partitionedQueues;
        /// <summary>
        /// A visitor that translates tx entry requests to CQL queries, 
        /// sends them to Cassandra, and collects results and fill the request's result fields.
        /// 
        /// Since the visitor maintains no states across individual invokations, 
        /// only one instance suffice for all invoking threads. 
        /// </summary>
        internal PartitionedCassandraVersionTableVisitor cassandraVisitor;

        public PartitionedCassandraVersionTable(VersionDb versionDb, string tableId, int partitionCount = 4)
            : base(versionDb, tableId)
        {
            this.PartitionCount = partitionCount;
            this.partitionedQueues = new RequestQueue<VersionEntryRequest>[partitionCount];
            for (int pid = 0; pid < this.PartitionCount; pid++)
            {
                this.partitionedQueues[pid] = new RequestQueue<VersionEntryRequest>(partitionCount);
                this.tableVisitors[pid] = new PartitionedCassandraVersionTableVisitor();
            }

            //for (int pk = 0; pk < partitionCount; pk++)
            //{
            //    Thread thread = new Thread(this.Monitor);
            //    thread.Start(pk);
            //}
        }

        //private void Monitor(object pk)
        //{
        //    int partitionKey = (int)pk;
        //    while (true)
        //    {
        //        VersionEntryRequest veReq = null;
        //        if (this.partitionedQueues[partitionKey].TryDequeue(out veReq))
        //        {
        //            cassandraVisitor.Visit(veReq);
        //        }
        //    }
        //}

        internal override void EnqueueVersionEntryRequest(VersionEntryRequest req, int execPartition = 0)
        {
            int pk = this.VersionDb.PhysicalTxPartitionByKey(req.RecordKey);
            partitionedQueues[pk].Enqueue(req, execPartition);

            //Console.WriteLine("Enqueue version entry req record key {0}", req.RecordKey.ToString());

            while (!req.Finished)
            {                
                lock (req)
                {
                    if (!req.Finished)
                    {
                        //System.Threading.Monitor.Wait(req);
                    } else
                    {
                        //Console.WriteLine("finished version entry req");
                    }
                }
            }
        }
    }


    /// <summary>
    /// CQL statements
    /// </summary>
    internal partial class PartitionedCassandraVersionTable
    {
        public static readonly string CQL_GET_VERSION_TOP_2 =
            "SELECT * FROM {0} WHERE recordKey = '{1}' ORDER BY versionKey DESC LIMIT 2";

        public static readonly string CQL_REPLACE_VERSION =
            "UPDATE {0} SET beginTimestamp={1}, endTimestamp={2}, txId={3} " +      // todo, ok
            "WHERE recordKey='{4}' AND versionKey={5}"; // + " AND txId={6} AND endTimestamp={7}";

        public static readonly string CQL_GET_VERSION_ENTRY =
            "SELECT * FROM {0} WHERE recordKey='{1}' AND versionKey={2}";

        public static readonly string CQL_REPLACE_WHOLE_VERSION =   // todo, ok
            "UPDATE {0} SET beginTimestamp={1}, endTimestamp={2}, record={3}, txId={4}, maxCommitTs={5} " +
            "WHERE recordKey='{6}' AND versionKey={7} ";

        public static readonly string CQL_UPLOAD_VERSION_ENTRY =    // todo, ok
            "INSERT INTO {0} (recordKey, versionKey, beginTimestamp, endTimestamp, record, txId, maxCommitTs) " +
            "VALUES ('{1}', {2}, {3}, {4}, {5}, {6}, {7})";

        public static readonly string CQL_UPDATE_MAX_COMMIT_TIMESTAMP =     // todo, ok
            "UPDATE {0} SET maxCommitTs = {1} " +
            "WHERE recordKey='{2}' AND versionKey={3} ";    // + " AND maxCommitTs < {1}";

        public static readonly string CQL_DELETE_VERSION_ENTRY =    // todo, ok
            "DELETE FROM {0} WHERE recordKey = '{1}' AND versionKey = {2}";

    }

    /// <summary>
    /// Version Table operation
    /// </summary>
    internal partial class PartitionedCassandraVersionTable
    {
        /// <summary>
        /// Execute the `cql` statement
        /// </summary>
        internal RowSet CQLExecute(string cql)
        {
            //CassandraSessionManager.CqlCnt += 1;
            //Console.WriteLine(cql);
            return this.SessionManager.GetSession(PartitionedCassandraVersionDb.DEFAULT_KEYSPACE).Execute(cql);
        }

        /// <summary>
        /// Execute statements with Light Weight Transaction (IF),
        /// result RowSet just has one row, whose `[applied]` column indicates 
        /// the execution's state
        /// NOTE: `CREATE TABLE IF ...` can not be executed with this function, 
        /// catch `AlreadyExistsException` instead
        /// </summary>
        /// <param name="cql"></param>
        /// <returns>applied or not</returns>
        internal bool CQLExecuteWithIfApplied(string cql)
        {
            //CassandraSessionManager.CqlIfCnt += 1;
            //Console.WriteLine(cql);
            var rs = this.SessionManager.GetSession(PartitionedCassandraVersionDb.DEFAULT_KEYSPACE).Execute(cql);
            var rse = rs.GetEnumerator();
            rse.MoveNext();
            return rse.Current.GetValue<bool>("[applied]");
        }

        internal override IEnumerable<VersionEntry> GetVersionList(object recordKey)
        {
            List<VersionEntry> entries = new List<VersionEntry>();
            var rs = this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_GET_VERSION_TOP_2,
                                                   this.tableId, recordKey.ToString()));
            foreach (var row in rs)
            {
                entries.Add(new VersionEntry(
                    row.GetValue<string>("recordkey"),
                    row.GetValue<long>("versionkey"),
                    row.GetValue<long>("begintimestamp"),
                    row.GetValue<long>("endtimestamp"),
                    BytesSerializer.Deserialize(row.GetValue<byte[]>("record")),
                    row.GetValue<long>("txid"),
                    row.GetValue<long>("maxcommitts")
                ));
            }

            return entries;
        }

        internal override IEnumerable<VersionEntry> InitializeAndGetVersionList(object recordKey)
        {
            VersionEntry emptyEntry = VersionEntry.InitEmptyVersionEntry(recordKey);

            try
            {
                this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_UPLOAD_VERSION_ENTRY,
                                                                    this.tableId,
                                                                    emptyEntry.RecordKey.ToString(),
                                                                    emptyEntry.VersionKey,
                                                                    emptyEntry.BeginTimestamp,
                                                                    emptyEntry.EndTimestamp,
                                                                    BytesSerializer.ToHexString(BytesSerializer.Serialize(emptyEntry.Record)),
                                                                    emptyEntry.TxId,
                                                                    emptyEntry.MaxCommitTs));
                return this.GetVersionList(recordKey);
            } catch (Cassandra.DriverException e)
            {
                return null;
            }
        }

        internal override VersionEntry ReplaceVersionEntry(object recordKey, long versionKey, long beginTimestamp, long endTimestamp, long txId, long readTxId, long expectedEndTimestamp)
        {
            // read first
            VersionEntry ve = this.GetVersionEntryByKey(recordKey, versionKey);
            if (ve.TxId == readTxId && ve.EndTimestamp == expectedEndTimestamp)
            {
                this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_REPLACE_VERSION,
                                                this.tableId, beginTimestamp, endTimestamp, txId,
                                                recordKey.ToString(), versionKey));
                ve.BeginTimestamp = beginTimestamp;
                ve.EndTimestamp = endTimestamp;
                ve.TxId = txId;
            }

            return ve;
        }

        internal override bool ReplaceWholeVersionEntry(object recordKey, long versionKey, VersionEntry versionEntry)
        {
            this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_REPLACE_WHOLE_VERSION,
                                                                    this.tableId, versionEntry.BeginTimestamp, versionEntry.EndTimestamp,
                                                                    BytesSerializer.ToHexString(BytesSerializer.Serialize(versionEntry.Record)),
                                                                    versionEntry.TxId, versionEntry.MaxCommitTs,
                                                                    versionEntry.RecordKey.ToString(), versionEntry.VersionKey));
            return true;
        }

        internal override bool UploadNewVersionEntry(object recordKey, long versionKey, VersionEntry versionEntry)
        {
            this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_UPLOAD_VERSION_ENTRY,
                                                      this.tableId,
                                                      versionEntry.RecordKey.ToString(),
                                                      versionEntry.VersionKey,
                                                      versionEntry.BeginTimestamp,
                                                      versionEntry.EndTimestamp,
                                                      BytesSerializer.ToHexString(BytesSerializer.Serialize(versionEntry.Record)),
                                                      versionEntry.TxId,
                                                      versionEntry.MaxCommitTs));
            return true;
        }

        internal override VersionEntry UpdateVersionMaxCommitTs(object recordKey, long versionKey, long commitTs)
        {
            VersionEntry ve = this.GetVersionEntryByKey(recordKey, versionKey);
            if (ve.MaxCommitTs < commitTs)
            {
                this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_UPDATE_MAX_COMMIT_TIMESTAMP,
                                            this.tableId, commitTs, recordKey.ToString(), versionKey));
                ve.MaxCommitTs = commitTs;
            }

            return ve;
        }

        internal override bool DeleteVersionEntry(object recordKey, long versionKey)
        {
            this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_DELETE_VERSION_ENTRY,
                                                       this.tableId, recordKey.ToString(), versionKey));
            return true;
        }

        internal Row GetRawVersionEntryByKey(object recordKey, long versionKey)
        {
            var rs = this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_GET_VERSION_ENTRY,
                                                    this.tableId, recordKey.ToString(), versionKey));
            var rse = rs.GetEnumerator();
            rse.MoveNext();
            Row row = rse.Current;
            return row;
        }

        internal override VersionEntry GetVersionEntryByKey(object recordKey, long versionKey)
        {
            var rs = this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_GET_VERSION_ENTRY,
                                                    this.tableId, recordKey.ToString(), versionKey));
            var rse = rs.GetEnumerator();
            rse.MoveNext();
            Row row = rse.Current;
            if (row == null)
            {
                return null;
            }

            return new VersionEntry(
                recordKey, versionKey, row.GetValue<long>("begintimestamp"),
                row.GetValue<long>("endtimestamp"),
                BytesSerializer.Deserialize(row.GetValue<byte[]>("record")),
                row.GetValue<long>("txid"),
                row.GetValue<long>("maxcommitts"));
        }

        internal override IDictionary<VersionPrimaryKey, VersionEntry> GetVersionEntryByKey(IEnumerable<VersionPrimaryKey> batch)
        {
            Dictionary<VersionPrimaryKey, VersionEntry> versionEntries = new Dictionary<VersionPrimaryKey, VersionEntry>();
            // sadly, there is no batch read method
            foreach (VersionPrimaryKey pk in batch)
            {
                versionEntries.Add(pk, this.GetVersionEntryByKey(pk.RecordKey, pk.VersionKey));
            }

            return versionEntries;
        }
    }
}
