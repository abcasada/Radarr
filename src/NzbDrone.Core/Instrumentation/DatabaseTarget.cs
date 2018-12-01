﻿using System.Data;
using System.Data.SQLite;
using NLog.Common;
using NLog.Config;
using NLog;
using NLog.Targets;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Instrumentation
{
    public class DatabaseTarget : TargetWithLayout, IHandle<ApplicationShutdownRequested>
    {
        private readonly SQLiteConnection _connection;

        private readonly IConnectionStringFactory _connectionStringFactory;

        const string INSERT_COMMAND = "INSERT INTO [Logs]([Message],[Time],[Logger],[Exception],[ExceptionType],[Level]) " +
                                      "VALUES(@Message,@Time,@Logger,@Exception,@ExceptionType,@Level)";

        public DatabaseTarget(IConnectionStringFactory connectionStringFactory)
        {
            _connectionStringFactory = connectionStringFactory;
        }

        public void Register()
        {
            var target = new SlowRunningAsyncTargetWrapper(this) { TimeToSleepBetweenBatches = 500 };

            Rule = new LoggingRule("*", LogLevel.Info, target);

            LogManager.Configuration.AddTarget("DbLogger", target);
            LogManager.Configuration.LoggingRules.Add(Rule);
            LogManager.ConfigurationReloaded += OnLogManagerOnConfigurationReloaded;
            LogManager.ReconfigExistingLoggers();
        }

        public void UnRegister()
        {
            LogManager.ConfigurationReloaded -= OnLogManagerOnConfigurationReloaded;
            LogManager.Configuration.RemoveTarget("DbLogger");
            LogManager.Configuration.LoggingRules.Remove(Rule);
            LogManager.ReconfigExistingLoggers();
            Dispose();
        }

        private void OnLogManagerOnConfigurationReloaded(object sender, LoggingConfigurationReloadedEventArgs args)
        {
            Register();
        }

        public LoggingRule Rule { get; set; }

        protected override void Write(LogEventInfo logEvent)
        {
            try
            {
                var log = new Log();
                log.Time = logEvent.TimeStamp;
                log.Message = CleanseLogMessage.Cleanse(logEvent.FormattedMessage);

                log.Logger = logEvent.LoggerName;

                if (log.Logger.StartsWith("NzbDrone."))
                {
                    log.Logger = log.Logger.Remove(0, 9);
                }

                if (logEvent.Exception != null)
                {
                    if (string.IsNullOrWhiteSpace(log.Message))
                    {
                        log.Message = logEvent.Exception.Message;
                    }
                    else
                    {
                        log.Message += ": " + logEvent.Exception.Message;
                    }

                    log.Exception = logEvent.Exception.ToString();
                    log.ExceptionType = logEvent.Exception.GetType().ToString();
                }

                log.Level = logEvent.Level.Name;

                using (var connection =
                    SQLiteFactory.Instance.CreateConnection())
                {
                    connection.ConnectionString = _connectionStringFactory.LogDbConnectionString;
                    using (var sqlCommand = connection.CreateCommand())
                    {
                        sqlCommand.CommandText = INSERT_COMMAND;
                        sqlCommand.Parameters.Add(new SQLiteParameter("Message", DbType.String) { Value = log.Message });
                        sqlCommand.Parameters.Add(new SQLiteParameter("Time", DbType.DateTime) { Value = log.Time.ToUniversalTime() });
                        sqlCommand.Parameters.Add(new SQLiteParameter("Logger", DbType.String) { Value = log.Logger });
                        sqlCommand.Parameters.Add(new SQLiteParameter("Exception", DbType.String) { Value = log.Exception });
                        sqlCommand.Parameters.Add(new SQLiteParameter("ExceptionType", DbType.String) { Value = log.ExceptionType });
                        sqlCommand.Parameters.Add(new SQLiteParameter("Level", DbType.String) { Value = log.Level });

                        sqlCommand.ExecuteNonQuery();   
                    }
                }

            }
            catch (SQLiteException ex)
            {
                InternalLogger.Error("Unable to save log event to database: {0}", ex);
                throw;
            }
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            if (LogManager.Configuration?.LoggingRules?.Contains(Rule) == true)
            {
                UnRegister();
            }
        }
    }
}