namespace NServiceBus.Transport.SQLServer
{
    using System;
    using System.Data;
    using System.Data.SqlClient;
    using System.Threading;
    using System.Threading.Tasks;
    using Logging;
    using Transports;
    using Unicast.Queuing;
    using static System.String;

    class TableBasedQueue
    {
        public TableBasedQueue(QueueAddress address)
        {
            var sanitizer = new SqlCommandBuilder();

            this.tableName = sanitizer.QuoteIdentifier(address.TableName);
            this.schemaName = sanitizer.QuoteIdentifier(address.SchemaName);

            this.TransportAddress = address.ToString();
        }

        public string TransportAddress { get; }

        public virtual async Task<int> TryPeek(SqlConnection connection, CancellationToken token, int timeoutInSeconds = 30)
        {
            var commandText = Format(Sql.PeekText, schemaName, tableName);

            using (var command = new SqlCommand(commandText, connection) {CommandTimeout = timeoutInSeconds})
            {
                var numberOfMessages = (int)await command.ExecuteScalarAsync(token).ConfigureAwait(false);

                return numberOfMessages;
            }
        }

        public virtual async Task<MessageReadResult> TryReceive(SqlConnection connection, SqlTransaction transaction)
        {
            var commandText = Format(Sql.ReceiveText, schemaName, tableName);

            using (var command = new SqlCommand(commandText, connection, transaction))
            {
                return await ReadMessage(command).ConfigureAwait(false);
            }
        }

        public Task DeadLetter(MessageRow poisonMessage, SqlConnection connection, SqlTransaction transaction)
        {
            return SendRawMessage(poisonMessage, connection, transaction);
        }

        public Task Send(OutgoingMessage message, SqlConnection connection, SqlTransaction transaction)
        {
            var messageRow = MessageRow.From(message.Headers, message.Body);

            return SendRawMessage(messageRow, connection, transaction);
        }

        static async Task<MessageReadResult> ReadMessage(SqlCommand command)
        {
            // We need sequential access to not buffer everything into memory
            using (var dataReader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow | CommandBehavior.SequentialAccess).ConfigureAwait(false))
            {
                if (!await dataReader.ReadAsync().ConfigureAwait(false))
                {
                    return MessageReadResult.NoMessage;
                }

                var readResult = await MessageRow.Read(dataReader).ConfigureAwait(false);

                return readResult;
            }
        }

        async Task SendRawMessage(MessageRow message, SqlConnection connection, SqlTransaction transaction)
        {
            var commandText = Format(Sql.SendText, schemaName, tableName);

            try
            {
                using (var command = new SqlCommand(commandText, connection, transaction))
                {
                    message.PrepareSendCommand(command);

                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            catch (SqlException ex)
            {
                if (ex.Number == 208)
                {
                    ThrowQueueNotFoundException(address, ex);
                }

                ThrowFailedToSendException(address, ex);
            }
            catch (Exception ex)
            {
                ThrowFailedToSendException(address, ex);
            }
        }

        static void ThrowQueueNotFoundException(QueueAddress destination, SqlException ex)
        {
            var msg = destination == null
                ? "Failed to send message. Target address is null."
                : $"Failed to send message to {destination}";

            throw new QueueNotFoundException(destination?.ToString(), msg, ex);
        }
        static void ThrowFailedToSendException(QueueAddress address, Exception ex)
        {
            if (address == null)
            {
                throw new Exception("Failed to send message.", ex);
            }
            throw new Exception($"Failed to send message to {address}", ex);
        }

        public async Task<int> Purge(SqlConnection connection)
        {
            var commandText = Format(Sql.PurgeText, schemaName, tableName);

            using (var command = new SqlCommand(commandText, connection))
            {
                return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        public async Task<int> PurgeBatchOfExpiredMessages(SqlConnection connection, int purgeBatchSize)
        {
            var commandText = Format(Sql.PurgeBatchOfExpiredMessagesText, purgeBatchSize, schemaName, tableName);

            using (var command = new SqlCommand(commandText, connection))
            {
                return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        public async Task LogWarningWhenIndexIsMissing(SqlConnection connection)
        {
            var commandText = Format(Sql.CheckIfExpiresIndexIsPresent, Sql.ExpiresIndexName, schemaName, tableName);

            using (var command = new SqlCommand(commandText, connection))
            {
                var rowsCount = (int) await command.ExecuteScalarAsync().ConfigureAwait(false);

                if (rowsCount == 0)
                {
                    Logger.WarnFormat(@"Table {0}.{1} does not contain index '{2}'." + Environment.NewLine + "Adding this index will speed up the process of purging expired messages from the queue. Please consult the documentation for further information.", schemaName, tableName, Sql.ExpiresIndexName);
                }
            }
        }

        public override string ToString()
        {
            return $"{schemaName}.{tableName}";
        }

        string tableName;
        string schemaName;

        static ILog Logger = LogManager.GetLogger(typeof(TableBasedQueue));
    }
}