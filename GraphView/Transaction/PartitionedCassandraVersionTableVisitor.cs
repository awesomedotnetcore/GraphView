﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cassandra;

namespace GraphView.Transaction
{
    internal partial class PartitionedCassandraVersionTableVisitor : VersionTableVisitor
    {
        private CassandraSessionManager SessionManager
        {
            get
            {
                return CassandraSessionManager.Instance;
            }
        }

        // these two `CQLExecute` and `CQLExecuteWithIfApplied` are the same 
        // as those in `CassandraVersionTable`
        internal RowSet CQLExecute(string cql)
        {
            return this.SessionManager.GetSession(CassandraVersionDb.DEFAULT_KEYSPACE).Execute(cql);
        }

        internal bool CQLExecuteWithIfApplied(string cql)
        {
            var rs = this.SessionManager.GetSession(CassandraVersionDb.DEFAULT_KEYSPACE).Execute(cql);
            var rse = rs.GetEnumerator();
            rse.MoveNext();
            return rse.Current.GetValue<bool>("[applied]");
        }

        internal override void Visit(DeleteVersionRequest req)
        {
            this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_DELETE_VERSION_ENTRY,
                                                       req.TableId, req.RecordKey.ToString(), req.VersionKey));
            req.Result = 1L;
            req.Finished = true;
        }

        internal override void Visit(GetVersionListRequest req)
        {
            var rs = this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_GET_VERSION_TOP_2,
                                                   req.TableId, req.RecordKey.ToString()));
            int cnt = 0;
            foreach (var row in rs)
            {
                req.LocalContainer[cnt].UpdateValue(
                    row.GetValue<string>("recordkey"),
                    row.GetValue<long>("versionkey"),
                    row.GetValue<long>("begintimestamp"),
                    row.GetValue<long>("endtimestamp"),
                    BytesSerializer.Deserialize(row.GetValue<byte[]>("record")),
                    row.GetValue<long>("txid"),
                    row.GetValue<long>("maxcommitts")
                );
                cnt += 1;
            }

            req.Result = cnt;
            req.Finished = true;
        }

        internal override void Visit(InitiGetVersionListRequest req)
        {
            VersionEntry emptyEntry = VersionEntry.InitEmptyVersionEntry(req.RecordKey);

            this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_UPLOAD_VERSION_ENTRY,
                                                                req.TableId,
                                                                emptyEntry.RecordKey.ToString(),
                                                                emptyEntry.VersionKey,
                                                                emptyEntry.BeginTimestamp,
                                                                emptyEntry.EndTimestamp,
                                                                BytesSerializer.ToHexString(BytesSerializer.Serialize(emptyEntry.Record)),
                                                                emptyEntry.TxId,
                                                                emptyEntry.MaxCommitTs));
            req.Result = true;
            req.Finished = true;
        }

        internal VersionEntry GetVersionEntryByKey(string tableId, object recordKey, long versionKey, VersionEntry ve)
        {
            var rs = this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_GET_VERSION_ENTRY,
                                                    tableId, recordKey.ToString(), versionKey));
            var rse = rs.GetEnumerator();
            rse.MoveNext();
            Row row = rse.Current;
            if (row == null)
            {
                return null;
            }

            ve.UpdateValue(
                recordKey, versionKey, row.GetValue<long>("begintimestamp"),
                row.GetValue<long>("endtimestamp"),
                BytesSerializer.Deserialize(row.GetValue<byte[]>("record")),
                row.GetValue<long>("txid"),
                row.GetValue<long>("maxcommitts"));
            return ve;
        }

        internal override void Visit(ReadVersionRequest req)
        {
            req.Result = this.GetVersionEntryByKey(req.TableId, req.RecordKey, req.VersionKey, req.LocalVerEntry);
            req.Finished = true;
        }

        internal override void Visit(ReplaceVersionRequest req)
        {
            // read first
            VersionEntry ve = this.GetVersionEntryByKey(req.TableId, req.RecordKey, req.VersionKey, req.LocalVerEntry);
            if (ve.TxId == req.ReadTxId && ve.EndTimestamp == req.ExpectedEndTs)
            {
                this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_REPLACE_VERSION,
                                                req.TableId, req.BeginTs, req.EndTs, req.TxId,
                                                req.RecordKey.ToString(), req.VersionKey));
                ve.BeginTimestamp = req.BeginTs;
                ve.EndTimestamp = req.EndTs;
                ve.TxId = req.TxId;
            }

            req.Result = ve;
            req.Finished = true;
        }

        internal override void Visit(ReplaceWholeVersionRequest req)
        {
            this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_REPLACE_WHOLE_VERSION,
                                        req.TableId, req.VersionEntry.BeginTimestamp, req.VersionEntry.EndTimestamp,
                                        BytesSerializer.ToHexString(BytesSerializer.Serialize(req.VersionEntry.Record)),
                                        req.VersionEntry.TxId, req.VersionEntry.MaxCommitTs,
                                        req.VersionEntry.RecordKey.ToString(), req.VersionEntry.VersionKey));
            req.Result = 1L;
            req.Finished = true;
        }

        internal override void Visit(UpdateVersionMaxCommitTsRequest req)
        {
            VersionEntry ve = this.GetVersionEntryByKey(req.TableId, req.RecordKey, req.VersionKey, req.LocalVerEntry);
            if (ve.MaxCommitTs < req.MaxCommitTs)
            {
                this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_UPDATE_MAX_COMMIT_TIMESTAMP,
                                               req.TableId, req.MaxCommitTs, req.RecordKey.ToString(), req.VersionKey));
                ve.MaxCommitTs = req.MaxCommitTs;
            }

            req.Result = ve;
            req.Finished = true;
        }

        internal override void Visit(UploadVersionRequest req)
        {
            this.CQLExecute(string.Format(PartitionedCassandraVersionTable.CQL_UPLOAD_VERSION_ENTRY,
                                                      req.TableId,
                                                      req.VersionEntry.RecordKey.ToString(),
                                                      req.VersionEntry.VersionKey,
                                                      req.VersionEntry.BeginTimestamp,
                                                      req.VersionEntry.EndTimestamp,
                                                      BytesSerializer.ToHexString(BytesSerializer.Serialize(req.VersionEntry.Record)),
                                                      req.VersionEntry.TxId,
                                                      req.VersionEntry.MaxCommitTs));
            req.Result = 1L;
            req.Finished = true;
        }
    }
}
