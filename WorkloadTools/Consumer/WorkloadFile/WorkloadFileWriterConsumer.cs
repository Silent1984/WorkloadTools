﻿using NLog;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace WorkloadTools.Consumer.WorkloadFile
{
    public class WorkloadFileWriterConsumer : BufferedWorkloadConsumer
    {

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public string OutputFile { get; set; }
        public static int CACHE_SIZE = 1000;
        // controls how often the data is written to the database
        // if not enough events are generated to flush the cache
        // a flush is forced every CACHE_FLUSH_HEARTBEAT_MINUTES 
        public static int CACHE_FLUSH_HEARTBEAT_MINUTES = 1;
        public DateTime lastFlush = DateTime.Now;

        private bool databaseInitialized = false;
        private int row_id = 1;
        private string connectionString;

        private readonly object syncRoot = new object();

        private SQLiteConnection conn;
        private SQLiteCommand events_cmd;
        private SQLiteCommand events_update_cmd;
        private SQLiteCommand waits_cmd;
        private SQLiteCommand counters_cmd;

        private long _rowsInserted = 0;

        private readonly string insert_events = @"
                INSERT INTO Events (
                    row_id,
                    event_sequence,
                    event_type,
                    start_time,
                    client_app_name,
                    client_host_name,
                    database_name,
                    server_principal_name,
                    session_id,
                    sql_text,
                    cpu,
                    duration,
                    reads,
                    writes
                )
                VALUES (
                    $row_id,
                    $event_sequence,
                    $event_type,
                    $start_time,
                    $client_app_name,
                    $client_host_name,
                    $database_name,
                    $server_principal_name,
                    $session_id,
                    $sql_text,
                    $cpu,
                    $duration,
                    $reads,
                    $writes
                );";

        private readonly string update_events = @"
                UPDATE Events SET cpu = $cpu,
                                  duration = $duration,
                                  reads = $reads,
                                  writes = $writes,
                                  sql_text = $sql_text
                WHERE row_id = (SELECT row_id 
                                FROM Events
                                WHERE session_id = $session_id
                                AND event_sequence < $event_sequence
                                AND IFNULL(duration, 0) = 0
                                AND event_type IN (2, -3) /* 2 = RPCStarting, -3 = BatchStarting */
                                ORDER BY EVENT_SEQUENCE DESC
                                LIMIT 1);";

        private readonly string insert_waits = @"
                INSERT INTO Waits (
                    row_id,
                    wait_type,
                    wait_sec,
                    resource_sec,
                    signal_sec,
                    wait_count
                )
                VALUES (
                    $row_id,
                    $wait_type,
                    $wait_sec,
                    $resource_sec,
                    $signal_sec,
                    $wait_count
                );";

        private readonly string insert_counters = @"
                INSERT INTO Counters (
                    row_id,
                    name,
                    value
                )
                VALUES (
                    $row_id,
                    $name,
                    $value
                );";

        private readonly Queue<WorkloadEvent> cache = new Queue<WorkloadEvent>(CACHE_SIZE);

        private bool forceFlush = false;

        public override void ConsumeBuffered(WorkloadEvent evt)
        {
            if (!databaseInitialized)
            {
                InitializeDatabase();
            }

            lock (syncRoot)
            {
                cache.Enqueue(evt);

                Flush();
            }
        }

        private void Flush()
        {
            if (DateTime.Now.Subtract(lastFlush).TotalMinutes >= CACHE_FLUSH_HEARTBEAT_MINUTES)
            {
                forceFlush = true;
            }

            if (cache.Count == CACHE_SIZE || forceFlush)
            {
                InitializeConnection();
                var tran = conn.BeginTransaction();
                try
                {
                    lock (syncRoot)
                    {
                        while (cache.Count > 0)
                        {
                            InsertEvent(cache.Dequeue());
                        }
                    }
                    tran.Commit();
                }
                catch
                {
                    try
                    {
                        tran.Rollback();
                    }
                    catch (Exception)
                    {
                        //swallow
                    }
                    throw;
                }
                finally
                {
                    lastFlush = DateTime.Now;
                    forceFlush = false;
                }
            }
        }

        /*
         * Initializes the database connection.
         * Connection string settings that affect performance:
         * - synchronous = off | full | normal
         * - journal mode = memory | delete | persist | off
         * - cache size = <number>
         * - temp store = memory
         * - locking mode = exclusive
         */
        private void InitializeConnection()
        {
            if (conn == null && connectionString != null)
            {
                conn = new SQLiteConnection(connectionString);
                conn.Open();
            }

            if (events_cmd == null)
            {
                events_cmd = new SQLiteCommand(insert_events, conn);
            }

            if (events_update_cmd == null)
            {
                events_update_cmd = new SQLiteCommand(update_events, conn);
            }

            if (waits_cmd == null)
            {
                waits_cmd = new SQLiteCommand(insert_waits, conn);
            }

            if (counters_cmd == null)
            {
                counters_cmd = new SQLiteCommand(insert_counters, conn);
            }
        }

        private void UpdateExecutionEvent(WorkloadEvent evnt)
        {
            var evt = (ExecutionWorkloadEvent)evnt;

            _ = events_update_cmd.Parameters.AddWithValue("$event_sequence", evt.EventSequence);
            _ = events_update_cmd.Parameters.AddWithValue("$session_id", evt.SPID);
            _ = events_update_cmd.Parameters.AddWithValue("$cpu", evt.CPU);
            _ = events_update_cmd.Parameters.AddWithValue("$duration", evt.Duration);
            _ = events_update_cmd.Parameters.AddWithValue("$reads", evt.Reads);
            _ = events_update_cmd.Parameters.AddWithValue("$writes", evt.Writes);
            _ = events_update_cmd.Parameters.AddWithValue("$sql_text", evt.Text);

            int rowcount;
            rowcount = events_update_cmd.ExecuteNonQuery();
            if (rowcount == 0)
            {
                logger.Debug("Starting event not found - " + $"EventSequence: {evt.EventSequence}");
            }
        }

        private void InsertExecutionEvent(WorkloadEvent evnt)
        {
            var evt = (ExecutionWorkloadEvent)evnt;

            _ = events_cmd.Parameters.AddWithValue("$row_id", row_id++);
            _ = events_cmd.Parameters.AddWithValue("$event_sequence", evt.EventSequence);
            _ = events_cmd.Parameters.AddWithValue("$event_type", evt.Type);
            _ = events_cmd.Parameters.AddWithValue("$start_time", evt.StartTime);
            _ = events_cmd.Parameters.AddWithValue("$client_app_name", evt.ApplicationName);
            _ = events_cmd.Parameters.AddWithValue("$client_host_name", evt.HostName);
            _ = events_cmd.Parameters.AddWithValue("$database_name", evt.DatabaseName);
            _ = events_cmd.Parameters.AddWithValue("$server_principal_name", evt.LoginName);
            _ = events_cmd.Parameters.AddWithValue("$session_id", evt.SPID);
            _ = events_cmd.Parameters.AddWithValue("$sql_text", evt.Text);
            _ = events_cmd.Parameters.AddWithValue("$cpu", evt.CPU);
            _ = events_cmd.Parameters.AddWithValue("$duration", evt.Duration);
            _ = events_cmd.Parameters.AddWithValue("$reads", evt.Reads);
            _ = events_cmd.Parameters.AddWithValue("$writes", evt.Writes);

            _ = events_cmd.ExecuteNonQuery();

        }

        private void InsertEvent(WorkloadEvent evnt)
        {
            try
            {
                if (evnt is ExecutionWorkloadEvent)
                {
                    if ((evnt.Type == WorkloadEvent.EventType.BatchCompleted) || (evnt.Type == WorkloadEvent.EventType.RPCCompleted))
                    {
                        UpdateExecutionEvent(evnt);
                    }
                    else
                    {
                        InsertExecutionEvent(evnt);
                    }
                }

                if (evnt is CounterWorkloadEvent)
                {
                    InsertCounterEvent(evnt);
                }

                if (evnt is WaitStatsWorkloadEvent)
                {
                    InsertWaitEvent(evnt);
                }

                _rowsInserted++;
                if ((_rowsInserted % CACHE_SIZE == 0) || forceFlush)
                {
                    if (forceFlush)
                    {
                        forceFlush = false;
                    }

                    logger.Info("{_rowsInserted} events saved", _rowsInserted);
                }
            }
            catch (Exception e)
            {
                if (stopped)
                {
                    return;
                }

                logger.Error(e, "Unable to write to the destination file");
                throw;
            }

        }

        private void InsertWaitEvent(WorkloadEvent evnt)
        {
            var evt = (WaitStatsWorkloadEvent)evnt;

            _ = events_cmd.Parameters.AddWithValue("$row_id", row_id++);
            _ = events_cmd.Parameters.AddWithValue("$event_sequence", null);
            _ = events_cmd.Parameters.AddWithValue("$event_type", evt.Type);
            _ = events_cmd.Parameters.AddWithValue("$start_time", evt.StartTime);
            _ = events_cmd.Parameters.AddWithValue("$client_app_name", null);
            _ = events_cmd.Parameters.AddWithValue("$client_host_name", null);
            _ = events_cmd.Parameters.AddWithValue("$database_name", null);
            _ = events_cmd.Parameters.AddWithValue("$server_principal_name", null);
            _ = events_cmd.Parameters.AddWithValue("$session_id", null);
            _ = events_cmd.Parameters.AddWithValue("$sql_text", null);
            _ = events_cmd.Parameters.AddWithValue("$cpu", null);
            _ = events_cmd.Parameters.AddWithValue("$duration", null);
            _ = events_cmd.Parameters.AddWithValue("$reads", null);
            _ = events_cmd.Parameters.AddWithValue("$writes", null);

            _ = events_cmd.ExecuteNonQuery();

            var tran = conn.BeginTransaction();
            try
            {

                foreach (DataRow dr in evt.Waits.Rows)
                {
                    _ = waits_cmd.Parameters.AddWithValue("$row_id", row_id);
                    _ = waits_cmd.Parameters.AddWithValue("$wait_type", dr["wait_type"]);
                    _ = waits_cmd.Parameters.AddWithValue("$wait_sec", dr["wait_sec"]);
                    _ = waits_cmd.Parameters.AddWithValue("$resource_sec", dr["resource_sec"]);
                    _ = waits_cmd.Parameters.AddWithValue("$signal_sec", dr["signal_sec"]);
                    _ = waits_cmd.Parameters.AddWithValue("$wait_count", dr["wait_count"]);

                    _ = waits_cmd.ExecuteNonQuery();
                }

                tran.Commit();
            }
            catch (Exception)
            {
                tran.Rollback();
                throw;
            }
        }

        private void InsertCounterEvent(WorkloadEvent evnt)
        {
            var evt = (CounterWorkloadEvent)evnt;

            _ = events_cmd.Parameters.AddWithValue("$row_id", row_id++);
            _ = events_cmd.Parameters.AddWithValue("$event_sequence", null);
            _ = events_cmd.Parameters.AddWithValue("$event_type", evt.Type);
            _ = events_cmd.Parameters.AddWithValue("$start_time", evt.StartTime);
            _ = events_cmd.Parameters.AddWithValue("$client_app_name", null);
            _ = events_cmd.Parameters.AddWithValue("$client_host_name", null);
            _ = events_cmd.Parameters.AddWithValue("$database_name", null);
            _ = events_cmd.Parameters.AddWithValue("$server_principal_name", null);
            _ = events_cmd.Parameters.AddWithValue("$session_id", null);
            _ = events_cmd.Parameters.AddWithValue("$sql_text", null);
            _ = events_cmd.Parameters.AddWithValue("$cpu", null);
            _ = events_cmd.Parameters.AddWithValue("$duration", null);
            _ = events_cmd.Parameters.AddWithValue("$reads", null);
            _ = events_cmd.Parameters.AddWithValue("$writes", null);

            _ = events_cmd.ExecuteNonQuery();

            var tran = conn.BeginTransaction();
            try
            {

                foreach (var dr in evt.Counters)
                {
                    _ = counters_cmd.Parameters.AddWithValue("$row_id", row_id);
                    _ = counters_cmd.Parameters.AddWithValue("$name", dr.Key.ToString());
                    _ = counters_cmd.Parameters.AddWithValue("$value", dr.Value);

                    _ = counters_cmd.ExecuteNonQuery();
                }

                tran.Commit();
            }
            catch (Exception)
            {
                tran.Rollback();
                throw;
            }
        }

        public void InitializeDatabase()
        {
            logger.Info("Writing event data to {OutputFile}", OutputFile);

            if (!File.Exists(OutputFile))
            {
                _ = Directory.CreateDirectory(Directory.GetParent(OutputFile).FullName);
                SQLiteConnection.CreateFile(OutputFile);
            }

            var sqlCreateTable = $@"
CREATE TABLE IF NOT EXISTS FileProperties (
    name TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Events (
    row_id INTEGER PRIMARY KEY,
    event_sequence INTEGER,
    event_type INTEGER,
    start_time date NOT NULL,
    client_app_name TEXT NULL, 
    client_host_name TEXT NULL, 
    database_name TEXT NULL, 
    server_principal_name TEXT NULL, 
    session_id INTEGER NULL, 
    sql_text  TEXT NULL,
    cpu INTEGER NULL,
    duration INTEGER NULL,
    reads INTEGER NULL,
    writes INTEGER NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS Index_Session_ID_Event_Sequence ON Events(
    session_id ASC,
    event_sequence DESC
);

CREATE INDEX IF NOT EXISTS Index_Start_Time_Row_ID ON Events(
    Start_Time ASC,
    Row_ID ASC
);

CREATE TABLE IF NOT EXISTS Counters (
    row_id INTEGER,
    name TEXT NULL,
    value FLOAT NULL
);

CREATE TABLE IF NOT EXISTS Waits (
    row_id INTEGER,
    wait_type TEXT NULL,
    wait_sec INTEGER NULL,
    resource_sec INTEGER NULL,
    signal_sec INTEGER NULL,
    wait_count INTEGER NULL 
);

INSERT INTO FileProperties (name, value)
SELECT 'FormatVersion','{Assembly.GetEntryAssembly().GetName().Version}'
 WHERE NOT EXISTS (SELECT *
                     FROM FileProperties
                    WHERE name = 'FormatVersion'
                  );";

            var sqlMaxSeq = @"SELECT COALESCE(MAX(row_id), 0) + 1 FROM Events;";

            connectionString = "Data Source=" + OutputFile + ";Version=3;Cache Size=10000;Locking Mode=Exclusive;Journal Mode=Memory;";

            using (var m_dbConnection = new SQLiteConnection(connectionString))
            {
                m_dbConnection.Open();
                try
                {
                    var command = new SQLiteCommand(sqlCreateTable, m_dbConnection);
                    _ = command.ExecuteNonQuery();

                    command = new SQLiteCommand(sqlMaxSeq, m_dbConnection);
                    row_id = Convert.ToInt32(command.ExecuteScalar());
                }
                catch (Exception)
                {
                    throw;
                }
            }

            databaseInitialized = true;

        }

        protected override void Dispose(bool disposing)
        {
            logger.Info("Closing the connection to the output file");

            forceFlush = true;
            if (conn != null)
            {
                Flush();
            }

            try
            {
                conn?.Close();
                conn?.Dispose();

                events_cmd?.Dispose();
                waits_cmd?.Dispose();
                counters_cmd?.Dispose();
            }
            catch (Exception)
            {
                //ignore
            }

            stopped = true;
        }

        public override bool HasMoreEvents()
        {
            return cache.Count > 0;
        }
    }
}
