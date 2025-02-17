﻿// Copyright 2016 Serilog Contributors
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting.Json;
using Blazor.Serilog.Sinks.Batch;
using Blazor.Serilog.Sinks.Extensions;

namespace Blazor.Serilog.Sinks.SQLite
{
    internal class SQLiteSink : BatchProvider, ILogEventSink
    {
        private readonly string _databasePath;
        private readonly IFormatProvider _formatProvider;
        private readonly bool _storeTimestampInUtc;
        private readonly uint _maxDatabaseSize;
        private readonly bool _rollOver;
        private readonly string _tableName;
        private readonly TimeSpan? _retentionPeriod;
        private readonly Timer _retentionTimer;
        private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fff";
        private const long BytesPerMb = 1_048_576;
        private const long MaxSupportedPages = 5_242_880;
        private const long MaxSupportedPageSize = 4096;
        private const long MaxSupportedDatabaseSize = unchecked(MaxSupportedPageSize * MaxSupportedPages) / 1048576;
        private static SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

        public SQLiteSink(
            string sqlLiteDbPath,
            string tableName,
            IFormatProvider formatProvider,
            bool storeTimestampInUtc,
            TimeSpan? retentionPeriod,
            TimeSpan? retentionCheckInterval,
            uint batchSize = 100,
            uint maxDatabaseSize = 10,
            bool rollOver = true,
            bool needAutoCreateTable = false) : base(batchSize: (int)batchSize, maxBufferSize: 100_000)
        {
            _databasePath = sqlLiteDbPath;
            _tableName = tableName;
            _formatProvider = formatProvider;
            _storeTimestampInUtc = storeTimestampInUtc;
            _maxDatabaseSize = maxDatabaseSize;
            _rollOver = rollOver;

            if (maxDatabaseSize > MaxSupportedDatabaseSize)
            {
                throw new Exception($"Database size greater than {MaxSupportedDatabaseSize} MB is not supported");
            }

            if (needAutoCreateTable)
            {
                InitializeDatabase();
            }

            if (retentionPeriod.HasValue)
            {
                // impose a min retention period of 15 minute
                var retentionCheckMinutes = 15;
                if (retentionCheckInterval.HasValue)
                {
                    retentionCheckMinutes = Math.Max(retentionCheckMinutes, retentionCheckInterval.Value.Minutes);
                }

                // impose multiple of 15 minute interval
                retentionCheckMinutes = (retentionCheckMinutes / 15) * 15;

                _retentionPeriod = new[] { retentionPeriod, TimeSpan.FromMinutes(30) }.Max();

                // check for retention at this interval - or use retentionPeriod if not specified
                _retentionTimer = new Timer(
                    (x) => { ApplyRetentionPolicy(); },
                    null,
                    TimeSpan.FromMinutes(0),
                    TimeSpan.FromMinutes(retentionCheckMinutes));
            }
        }

        #region ILogEvent implementation

        public void Emit(LogEvent logEvent)
        {
            PushEvent(logEvent);
        }

        #endregion

        private void InitializeDatabase()
        {
            using (var conn = GetSqLiteConnection())
            {
                using (var command = conn.CreateCommand())
                {
                    command.CommandText = "PRAGMA journal_mode=WAL;";
                    command.ExecuteNonQuery();
                }
                CreateSqlTable(conn);
            }
        }

        private SqliteConnection GetSqLiteConnection()
        {
            var sqlConString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Cache = SqliteCacheMode.Shared,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            var sqLiteConnection = new SqliteConnection(sqlConString);
            sqLiteConnection.Open();

            return sqLiteConnection;
        }

        private void CreateSqlTable(SqliteConnection sqlConnection)
        {
            var colDefs = "Id INTEGER PRIMARY KEY AUTOINCREMENT,";
            colDefs += "TimeStamp TEXT,";
            colDefs += "Level VARCHAR(10),";
            colDefs += "Exception TEXT,";
            colDefs += "Message TEXT,";
            colDefs += "Properties TEXT,";
            colDefs += "MessageTemplate TEXT,";
            colDefs += "LogEvent TEXT,";
            colDefs += "UserName TEXT,";
            colDefs += "ClientIP TEXT,";
            colDefs += "ClientAgent TEXT";

            var sqlCreateText = $"CREATE TABLE IF NOT EXISTS {_tableName} ({colDefs})";

            var sqlCommand = new SqliteCommand(sqlCreateText, sqlConnection);
            sqlCommand.ExecuteNonQuery();
        }

        private SqliteCommand CreateSqlInsertCommand(SqliteConnection connection)
        {
            var sqlInsertText = "INSERT INTO {0} (Timestamp, Level, Exception, Message, Properties,MessageTemplate,LogEvent,UserName,ClientIP,ClientAgent)";
            sqlInsertText += " VALUES (@timeStamp, @level, @exception, @message, @properties,@messageTemplate,@logEvent,@userName,@clientIP,@clientAgent)";
            sqlInsertText = string.Format(sqlInsertText, _tableName);

            var sqlCommand = connection.CreateCommand();
            sqlCommand.CommandText = sqlInsertText;
            sqlCommand.CommandType = CommandType.Text;

            sqlCommand.Parameters.Add(new SqliteParameter("@timeStamp", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@level", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@exception", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@message", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@properties", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@messageTemplate", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@logEvent", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@userName", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@clientIP", DbType.String));
            sqlCommand.Parameters.Add(new SqliteParameter("@clientAgent", DbType.String));

            return sqlCommand;
        }

        private void ApplyRetentionPolicy()
        {
            var epoch = DateTimeOffset.Now.Subtract(_retentionPeriod.Value);
            using (var sqlConnection = GetSqLiteConnection())
            {
                using (var cmd = CreateSqlDeleteCommand(sqlConnection, epoch))
                {
                    SelfLog.WriteLine("Deleting log entries older than {0}", epoch);
                    var ret = cmd.ExecuteNonQuery();
                    SelfLog.WriteLine($"{ret} records deleted");
                }
            }
        }

        private void TruncateLog(SqliteConnection sqlConnection)
        {
            var cmd = sqlConnection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {_tableName}";
            cmd.ExecuteNonQuery();

            VacuumDatabase(sqlConnection);
        }

        private void VacuumDatabase(SqliteConnection sqlConnection)
        {
            var cmd = sqlConnection.CreateCommand();
            cmd.CommandText = "vacuum";
            cmd.ExecuteNonQuery();
        }

        private SqliteCommand CreateSqlDeleteCommand(SqliteConnection sqlConnection, DateTimeOffset epoch)
        {
            var cmd = sqlConnection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {_tableName} WHERE Timestamp < @epoch";
            cmd.Parameters.Add(
                new SqliteParameter("@epoch", DbType.String)
                {
                    Value = (_storeTimestampInUtc ? epoch.ToUniversalTime() : epoch).ToString(
                        TimestampFormat)
                });

            return cmd;
        }

        protected override async Task<bool> WriteLogEventAsync(ICollection<LogEvent> logEventsBatch)
        {
            if ((logEventsBatch == null) || (logEventsBatch.Count == 0))
                return true;
            await semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var sqlConnection = GetSqLiteConnection())
                {
                    try
                    {
                        await WriteToDatabaseAsync(logEventsBatch, sqlConnection).ConfigureAwait(false);
                        return true;
                    }
                    catch (SqliteException e)
                    {
                        SelfLog.WriteLine(e.Message);

                        if (e.SqliteErrorCode != SQLitePCL.raw.SQLITE_FULL)
                            return false;

                        if (_rollOver == false)
                        {
                            SelfLog.WriteLine("Discarding log excessive of max database");
                            return true;
                        }

                        var dbExtension = Path.GetExtension(_databasePath);

                        var newFilePath = Path.Combine(Path.GetDirectoryName(_databasePath) ?? "Logs",
                            $"{Path.GetFileNameWithoutExtension(_databasePath)}-{DateTime.Now:yyyyMMdd_HHmmss.ff}{dbExtension}");

                        File.Copy(_databasePath, newFilePath, true);

                        TruncateLog(sqlConnection);
                        await WriteToDatabaseAsync(logEventsBatch, sqlConnection).ConfigureAwait(false);

                        SelfLog.WriteLine($"Rolling database to {newFilePath}");
                        return true;
                    }
                    catch (Exception e)
                    {
                        SelfLog.WriteLine(e.Message);
                        return false;
                    }
                }
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }

        private async Task WriteToDatabaseAsync(ICollection<LogEvent> logEventsBatch, SqliteConnection sqlConnection)
        {
            using (var tr = sqlConnection.BeginTransaction())
            {
                using (var sqlCommand = CreateSqlInsertCommand(sqlConnection))
                {
                    sqlCommand.Transaction = tr;

                    foreach (var logEvent in logEventsBatch)
                    {
                        sqlCommand.Parameters["@timeStamp"].Value = _storeTimestampInUtc
                            ? logEvent.Timestamp.ToUniversalTime().ToString(TimestampFormat)
                            : logEvent.Timestamp.ToString(TimestampFormat);
                        sqlCommand.Parameters["@level"].Value = logEvent.Level.ToString();
                        sqlCommand.Parameters["@exception"].Value =
                            logEvent.Exception?.ToString() ?? string.Empty;
                        sqlCommand.Parameters["@message"].Value = logEvent.MessageTemplate.Render(logEvent.Properties, _formatProvider);
                        sqlCommand.Parameters["@messageTemplate"].Value = logEvent.MessageTemplate.Text;
                        sqlCommand.Parameters["@properties"].Value = logEvent.Properties.Count > 0 ? logEvent.Properties.Json() : string.Empty;
                        sqlCommand.Parameters["@logEvent"].Value = LogEventToJson(logEvent, _formatProvider);
                        sqlCommand.Parameters["@userName"].Value = logEvent.Properties.ContainsKey("UserName") ? logEvent.Properties["UserName"].ToString() : string.Empty;
                        sqlCommand.Parameters["@clientIP"].Value = logEvent.Properties.ContainsKey("ClientIP") ? logEvent.Properties["ClientIP"].ToString() : string.Empty;
                        sqlCommand.Parameters["@clientAgent"].Value = logEvent.Properties.ContainsKey("ClientAgent") ? logEvent.Properties["ClientAgent"].ToString() : string.Empty;
                        await sqlCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                    tr.Commit();
                }
            }
        }
        private object LogEventToJson(LogEvent logEvent, IFormatProvider formatProvider)
        {
            var jsonFormatter = new JsonFormatter(formatProvider: formatProvider);

            var sb = new StringBuilder();
            using (var writer = new StringWriter(sb))
                jsonFormatter.Format(logEvent, writer);
            return sb.ToString();
        }
    }
}
