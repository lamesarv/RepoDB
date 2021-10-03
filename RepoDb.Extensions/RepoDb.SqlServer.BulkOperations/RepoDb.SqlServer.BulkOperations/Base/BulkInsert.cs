﻿using RepoDb.Extensions;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using RepoDb.Interfaces;

namespace RepoDb
{
    public static partial class SqlConnectionExtension
    {
        #region BulkInsertInternalBase

        /// <summary>
        ///
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <param name="connection"></param>
        /// <param name="tableName"></param>
        /// <param name="entities"></param>
        /// <param name="dbFields"></param>
        /// <param name="mappings"></param>
        /// <param name="options"></param>
        /// <param name="hints"></param>
        /// <param name="bulkCopyTimeout"></param>
        /// <param name="batchSize"></param>
        /// <param name="isReturnIdentity"></param>
        /// <param name="usePhysicalPseudoTempTable"></param>
        /// <param name="transaction"></param>
        /// <returns></returns>
        private static int BulkInsertInternalBase<TEntity>(SqlConnection connection,
            string tableName,
            IEnumerable<TEntity> entities,
            IEnumerable<DbField> dbFields = null,
            IEnumerable<BulkInsertMapItem> mappings = null,
            SqlBulkCopyOptions options = default,
            string hints = null,
            int? bulkCopyTimeout = null,
            int? batchSize = null,
            bool? isReturnIdentity = null,
            bool? usePhysicalPseudoTempTable = null,
            SqlTransaction transaction = null)
            where TEntity : class
        {
            // Validate
            // ThrowIfNullOrEmpty(entities);

            // Variables needed
            var dbSetting = connection.GetDbSetting();
            var hasTransaction = transaction != null;
            int result;

            // Check the transaction
            if (transaction == null)
            {
                // Add the transaction if not present
                transaction = (SqlTransaction)connection.EnsureOpen().BeginTransaction();
            }
            else
            {
                // Validate the objects
                ValidateTransactionConnectionObject(connection, transaction);
            }

            try
            {
                // Get the DB Fields
                dbFields ??= DbFieldCache.Get(connection, tableName, transaction, true);

                // Variables needed
                var identityDbField = dbFields?.FirstOrDefault(dbField => dbField.IsIdentity);
                var entityType = entities?.FirstOrDefault()?.GetType() ?? typeof(TEntity);
                var entityFields = entityType.IsDictionaryStringObject() ?
                    GetDictionaryStringObjectFields(entities?.FirstOrDefault() as IDictionary<string, object>) :
                    FieldCache.Get(entityType);
                var fields = dbFields?.Select(dbField => dbField.AsField());

                // Filter the fields (based on mappings)
                if (mappings?.Any() == true)
                {
                    fields = fields?
                        .Where(e =>
                            mappings.Any(mapping => string.Equals(mapping.DestinationColumn, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }
                else
                {
                    // Filter the fields (based on the data entity)
                    if (entityFields?.Any() == true)
                    {
                        fields = fields?
                            .Where(e =>
                                entityFields.Any(f => string.Equals(f.Name, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                    }

                    // Explicitly define the mappings
                    mappings = fields?
                        .Select(e =>
                            new BulkInsertMapItem(e.Name, e.Name));
                }

                // Throw an error if there are no fields
                if (fields?.Any() != true)
                {
                    throw new MissingFieldException("There are no field(s) found for this operation.");
                }

                // Pseudo temp table
                var withPseudoExecution = isReturnIdentity == true && identityDbField != null;
                var tempTableName = CreateTempTableIfNecessary(connection,
                    tableName,
                    usePhysicalPseudoTempTable,
                    transaction,
                    withPseudoExecution,
                    dbSetting,
                    fields);

                // WriteToServer
                result = WriteToServerInternal<TEntity>(connection,
                    tempTableName ?? tableName,
                    entities,
                    mappings,
                    options,
                    bulkCopyTimeout,
                    batchSize,
                    withPseudoExecution,
                    transaction);

                // Check if this is with pseudo
                if (withPseudoExecution)
                {
                    // Merge the actual data
                    var sql = GetBulkInsertSqlText(tableName,
                        tempTableName,
                        fields,
                        identityDbField?.AsField(),
                        hints,
                        dbSetting,
                        withPseudoExecution);

                    // Execute the SQL
                    using (var reader = (DbDataReader)connection.ExecuteReader(sql, commandTimeout: bulkCopyTimeout, transaction: transaction))
                    {
                        var mapping = mappings?.FirstOrDefault(e => string.Equals(e.DestinationColumn, identityDbField.Name, StringComparison.OrdinalIgnoreCase));
                        var identityField = mapping != null ? new Field(mapping.SourceColumn) : identityDbField.AsField();
                        result = SetIdentityForEntities(entities, reader, identityField);
                    }

                    // Drop the table after used
                    sql = GetDropTemporaryTableSqlText(tempTableName, dbSetting);
                    connection.ExecuteNonQuery(sql, transaction: transaction);
                }

                // Commit the transaction
                if (hasTransaction == false)
                {
                    transaction?.Commit();
                }
            }
            catch
            {
                // Rollback the transaction
                if (hasTransaction == false)
                {
                    transaction?.Rollback();
                }

                // Throw
                throw;
            }
            finally
            {
                // Dispose the transaction
                if (hasTransaction == false)
                {
                    transaction?.Dispose();
                }
            }

            // Return the result
            return result;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="tableName"></param>
        /// <param name="reader"></param>
        /// <param name="dbFields"></param>
        /// <param name="mappings"></param>
        /// <param name="options"></param>
        /// <param name="bulkCopyTimeout"></param>
        /// <param name="batchSize"></param>
        /// <param name="transaction"></param>
        /// <returns></returns>
        internal static int BulkInsertInternalBase(SqlConnection connection,
            string tableName,
            DbDataReader reader,
            IEnumerable<DbField> dbFields = null,
            IEnumerable<BulkInsertMapItem> mappings = null,
            SqlBulkCopyOptions options = default,
            int? bulkCopyTimeout = null,
            int? batchSize = null,
            SqlTransaction transaction = null)
        {
            // Validate
            if (!reader.HasRows)
            {
                return default;
            }

            // Variables needed
            var hasTransaction = transaction != null;
            int result;

            // Check the transaction
            if (transaction == null)
            {
                // Add the transaction if not present
                transaction = (SqlTransaction)connection.EnsureOpen().BeginTransaction();
            }
            else
            {
                // Validate the objects
                ValidateTransactionConnectionObject(connection, transaction);
            }

            try
            {
                // Get the DB Fields
                dbFields ??= DbFieldCache.Get(connection, tableName, transaction, true);

                // Variables needed
                var readerFields = Enumerable
                    .Range(0, reader.FieldCount)
                    .Select(index => reader.GetName(index));
                var fields = dbFields?.Select(dbField => dbField.AsField());

                // Filter the fields (based on mappings)
                if (mappings?.Any() == true)
                {
                    fields = fields?
                        .Where(e =>
                            mappings.Any(mapping => string.Equals(mapping.DestinationColumn, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }
                else
                {
                    // Filter the fields (based on the data reader)
                    if (readerFields.Any() == true)
                    {
                        fields = fields?
                            .Where(e =>
                                readerFields.Any(fieldName => string.Equals(fieldName, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                    }

                    // Explicitly define the mappings
                    mappings = fields?
                        .Select(e =>
                            new BulkInsertMapItem(e.Name, e.Name));
                }

                // Throw an error if there are no fields
                if (fields?.Any() != true)
                {
                    throw new MissingFieldException("There are no field(s) found for this operation.");
                }

                // WriteToServer
                result = WriteToServerInternal(connection,
                    tableName,
                    reader,
                    mappings,
                    options,
                    bulkCopyTimeout,
                    batchSize,
                    transaction);

                // Commit the transaction
                if (hasTransaction == false)
                {
                    transaction.Commit();
                }
            }
            catch
            {
                // Rollback the transaction
                if (hasTransaction == false)
                {
                    transaction?.Rollback();
                }

                // Throw
                throw;
            }
            finally
            {
                // Dispose the transaction
                if (hasTransaction == false)
                {
                    transaction?.Dispose();
                }
            }

            // Return the result
            return result;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="tableName"></param>
        /// <param name="dataTable"></param>
        /// <param name="rowState"></param>
        /// <param name="dbFields"></param>
        /// <param name="mappings"></param>
        /// <param name="options"></param>
        /// <param name="hints"></param>
        /// <param name="bulkCopyTimeout"></param>
        /// <param name="batchSize"></param>
        /// <param name="isReturnIdentity"></param>
        /// <param name="usePhysicalPseudoTempTable"></param>
        /// <param name="transaction"></param>
        /// <returns></returns>
        internal static int BulkInsertInternalBase(SqlConnection connection,
            string tableName,
            DataTable dataTable,
            DataRowState? rowState = null,
            IEnumerable<DbField> dbFields = null,
            IEnumerable<BulkInsertMapItem> mappings = null,
            SqlBulkCopyOptions options = default,
            string hints = null,
            int? bulkCopyTimeout = null,
            int? batchSize = null,
            bool? isReturnIdentity = null,
            bool? usePhysicalPseudoTempTable = null,
            SqlTransaction transaction = null)
        {
            // Validate
            if (dataTable?.Rows.Count <= 0)
            {
                return default;
            }

            // Variables needed
            var dbSetting = connection.GetDbSetting();
            var hasTransaction = transaction != null;
            int result;

            // Check the transaction
            if (transaction == null)
            {
                // Add the transaction if not present
                transaction = (SqlTransaction)connection.EnsureOpen().BeginTransaction();
            }
            else
            {
                // Validate the objects
                ValidateTransactionConnectionObject(connection, transaction);
            }

            try
            {
                // Get the DB Fields
                dbFields ??= DbFieldCache.Get(connection, tableName, transaction, true);

                // Variables needed
                var identityDbField = dbFields?.FirstOrDefault(dbField => dbField.IsIdentity);
                var tableFields = GetDataColumns(dataTable)
                    .Select(column => column.ColumnName);
                var fields = dbFields?.Select(dbField => dbField.AsField());

                // Filter the fields (based on mappings)
                if (mappings?.Any() == true)
                {
                    fields = fields?
                        .Where(e =>
                            mappings.Any(mapping => string.Equals(mapping.DestinationColumn, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }
                else
                {
                    // Filter the fields (based on the data table)
                    if (tableFields?.Any() == true)
                    {
                        fields = fields?
                            .Where(e =>
                                tableFields.Any(fieldName => string.Equals(fieldName, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                    }

                    // Explicitly define the mappings
                    mappings = fields?
                        .Select(e =>
                            new BulkInsertMapItem(e.Name, e.Name));
                }

                // Throw an error if there are no fields
                if (fields?.Any() != true)
                {
                    throw new MissingFieldException("There are no field(s) found for this operation.");
                }

                // Pseudo temp table
                var withPseudoExecution = (isReturnIdentity == true && identityDbField != null);
                var tempTableName = CreateTempTableIfNecessary(connection,
                    tableName,
                    usePhysicalPseudoTempTable,
                    transaction,
                    withPseudoExecution,
                    dbSetting,
                    fields);

                // WriteToServer
                result = WriteToServerInternal(connection,
                    tempTableName ?? tableName,
                    dataTable,
                    rowState,
                    mappings,
                    options,
                    bulkCopyTimeout,
                    batchSize,
                    withPseudoExecution,
                    transaction);

                // Check if this is with pseudo
                if (withPseudoExecution)
                {
                    if (isReturnIdentity == true)
                    {
                        var sql = GetBulkInsertSqlText(tableName,
                            tempTableName,
                            fields,
                            identityDbField?.AsField(),
                            hints,
                            dbSetting,
                            withPseudoExecution);

                        // Identify the column
                        var column = dataTable.Columns[identityDbField.Name];
                        if (column?.ReadOnly == false)
                        {
                            using var reader = (DbDataReader)connection.ExecuteReader(sql, commandTimeout: bulkCopyTimeout, transaction: transaction);

                            result = SetIdentityForEntities(dataTable, reader, column);
                        }
                        else
                        {
                            result = connection.ExecuteNonQuery(sql, commandTimeout: bulkCopyTimeout, transaction: transaction);
                        }

                        // Drop the table after used
                        sql = GetDropTemporaryTableSqlText(tempTableName, dbSetting);
                        connection.ExecuteNonQuery(sql, transaction: transaction);
                    }
                }

                // Commit the transaction
                if (hasTransaction == false)
                {
                    transaction?.Commit();
                }

                // Return the result
                return result;
            }
            catch
            {
                // Rollback the transaction
                if (hasTransaction == false)
                {
                    transaction?.Rollback();
                }

                // Throw
                throw;
            }
            finally
            {
                // Dispose the transaction
                if (hasTransaction == false)
                {
                    transaction?.Dispose();
                }
            }
        }

        #endregion

        #region BulkInsertAsyncInternalBase

        /// <summary>
        ///
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <param name="connection"></param>
        /// <param name="tableName"></param>
        /// <param name="entities"></param>
        /// <param name="dbFields"></param>
        /// <param name="mappings"></param>
        /// <param name="options"></param>
        /// <param name="hints"></param>
        /// <param name="bulkCopyTimeout"></param>
        /// <param name="batchSize"></param>
        /// <param name="isReturnIdentity"></param>
        /// <param name="usePhysicalPseudoTempTable"></param>
        /// <param name="transaction"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<int> BulkInsertAsyncInternalBase<TEntity>(SqlConnection connection,
            string tableName,
            IEnumerable<TEntity> entities,
            IEnumerable<DbField> dbFields = null,
            IEnumerable<BulkInsertMapItem> mappings = null,
            SqlBulkCopyOptions options = default,
            string hints = null,
            int? bulkCopyTimeout = null,
            int? batchSize = null,
            bool? isReturnIdentity = null,
            bool? usePhysicalPseudoTempTable = null,
            SqlTransaction transaction = null,
            CancellationToken cancellationToken = default)
            where TEntity : class
        {
            // Validate
            if (entities?.Any() != true)
            {
                return default;
            }

            // Variables needed
            var dbSetting = connection.GetDbSetting();
            var hasTransaction = transaction != null;
            int result;

            // Check the transaction
            if (transaction == null)
            {
                // Add the transaction if not present
                transaction = (SqlTransaction)(await connection.EnsureOpenAsync(cancellationToken)).BeginTransaction();
            }
            else
            {
                // Validate the objects
                ValidateTransactionConnectionObject(connection, transaction);
            }

            try
            {
                // Get the DB Fields
                dbFields ??= await DbFieldCache.GetAsync(connection, tableName, transaction, true, cancellationToken: cancellationToken);

                // Variables needed
                var identityDbField = dbFields?.FirstOrDefault(dbField => dbField.IsIdentity);
                var entityType = entities.FirstOrDefault()?.GetType() ?? typeof(TEntity);
                var entityFields = entityType.IsDictionaryStringObject() ?
                    GetDictionaryStringObjectFields(entities.FirstOrDefault() as IDictionary<string, object>) :
                    FieldCache.Get(entityType);
                var fields = dbFields?.Select(dbField => dbField.AsField());

                // Filter the fields (based on mappings)
                if (mappings?.Any() == true)
                {
                    fields = fields?
                        .Where(e =>
                            mappings.Any(mapping => string.Equals(mapping.DestinationColumn, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }
                else
                {
                    // Filter the fields (based on the data entity)
                    if (entityFields?.Any() == true)
                    {
                        fields = fields?
                            .Where(e =>
                                entityFields.Any(f => string.Equals(f.Name, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                    }

                    // Explicitly define the mappings
                    mappings = fields?
                        .Select(e =>
                            new BulkInsertMapItem(e.Name, e.Name));
                }

                // Throw an error if there are no fields
                if (fields?.Any() != true)
                {
                    throw new MissingFieldException("There are no field(s) found for this operation.");
                }

                var withPseudoExecution = isReturnIdentity == true && identityDbField != null;
                var tempTableName = await CreateTempTableIfNecessaryAsync(connection,
                    tableName,
                    usePhysicalPseudoTempTable,
                    transaction,
                    withPseudoExecution,
                    dbSetting,
                    fields,
                    cancellationToken);

                // WriteToServer
                result = await WriteToServerAsyncInternal(connection,
                    tempTableName ?? tableName,
                    entities,
                    mappings,
                    options,
                    bulkCopyTimeout,
                    batchSize,
                    withPseudoExecution,
                    transaction,
                    cancellationToken);

                // Check if this is with pseudo
                if (withPseudoExecution)
                {
                    // Merge the actual data
                    var sql = GetBulkInsertSqlText(tableName,
                        tempTableName,
                        fields,
                        identityDbField?.AsField(),
                        hints,
                        dbSetting,
                        withPseudoExecution);

                    // Execute the SQL
                    using (var reader = (DbDataReader)(await connection.ExecuteReaderAsync(sql, commandTimeout: bulkCopyTimeout, transaction: transaction, cancellationToken: cancellationToken)))
                    {
                        result = await SetIdentityForEntitiesAsync(entities, reader, identityDbField, cancellationToken);
                    }

                    // Drop the table after used
                    sql = GetDropTemporaryTableSqlText(tempTableName, dbSetting);
                    await connection.ExecuteNonQueryAsync(sql, transaction: transaction, cancellationToken: cancellationToken);
                }

                // Commit the transaction
                if (hasTransaction == false)
                {
                    transaction?.Commit();
                }
            }
            catch
            {
                // Rollback the transaction
                if (hasTransaction == false)
                {
                    transaction?.Rollback();
                }

                // Throw
                throw;
            }
            finally
            {
                // Dispose the transaction
                if (hasTransaction == false)
                {
                    transaction?.Dispose();
                }
            }

            // Return the result
            return result;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="tableName"></param>
        /// <param name="reader"></param>
        /// <param name="dbFields"></param>
        /// <param name="mappings"></param>
        /// <param name="options"></param>
        /// <param name="bulkCopyTimeout"></param>
        /// <param name="batchSize"></param>
        /// <param name="transaction"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal static async Task<int> BulkInsertAsyncInternalBase(SqlConnection connection,
            string tableName,
            DbDataReader reader,
            IEnumerable<DbField> dbFields = null,
            IEnumerable<BulkInsertMapItem> mappings = null,
            SqlBulkCopyOptions options = default,
            int? bulkCopyTimeout = null,
            int? batchSize = null,
            SqlTransaction transaction = null,
            CancellationToken cancellationToken = default)
        {
            // Validate
            if (!reader.HasRows)
            {
                return default;
            }

            // Variables needed
            var hasTransaction = transaction != null;
            int result;

            // Check the transaction
            if (transaction == null)
            {
                // Add the transaction if not present
                transaction = (SqlTransaction)(await connection.EnsureOpenAsync(cancellationToken)).BeginTransaction();
            }
            else
            {
                // Validate the objects
                ValidateTransactionConnectionObject(connection, transaction);
            }

            try
            {
                // Get the DB Fields
                dbFields ??= await DbFieldCache.GetAsync(connection, tableName, transaction, true, cancellationToken);

                // Variables needed
                var readerFields = Enumerable
                    .Range(0, reader.FieldCount)
                    .Select(index => reader.GetName(index));
                var fields = dbFields?.Select(dbField => dbField.AsField());

                // Filter the fields (based on mappings)
                if (mappings?.Any() == true)
                {
                    fields = fields?
                        .Where(e =>
                            mappings.Any(mapping => string.Equals(mapping.DestinationColumn, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }
                else
                {
                    // Filter the fields (based on the data reader)
                    if (readerFields.Any() == true)
                    {
                        fields = fields?
                            .Where(e =>
                                readerFields.Any(fieldName => string.Equals(fieldName, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                    }

                    // Explicitly define the mappings
                    mappings = fields?
                        .Select(e =>
                            new BulkInsertMapItem(e.Name, e.Name));
                }

                // Throw an error if there are no fields
                if (fields?.Any() != true)
                {
                    throw new MissingFieldException("There are no field(s) found for this operation.");
                }

                // WriteToServer
                result = await WriteToServerAsyncInternal(connection,
                    tableName,
                    reader,
                    mappings,
                    options,
                    bulkCopyTimeout,
                    batchSize,
                    transaction,
                    cancellationToken);

                // Commit the transaction
                if (hasTransaction == false)
                {
                    transaction.Commit();
                }
            }
            catch
            {
                // Rollback the transaction
                if (hasTransaction == false)
                {
                    transaction.Rollback();
                }

                // Throw
                throw;
            }
            finally
            {
                // Dispose the transaction
                if (hasTransaction == false)
                {
                    transaction.Dispose();
                }
            }

            // Return the result
            return result;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="tableName"></param>
        /// <param name="dataTable"></param>
        /// <param name="rowState"></param>
        /// <param name="dbFields"></param>
        /// <param name="mappings"></param>
        /// <param name="options"></param>
        /// <param name="hints"></param>
        /// <param name="bulkCopyTimeout"></param>
        /// <param name="batchSize"></param>
        /// <param name="isReturnIdentity"></param>
        /// <param name="usePhysicalPseudoTempTable"></param>
        /// <param name="transaction"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal static async Task<int> BulkInsertAsyncInternalBase(SqlConnection connection,
            string tableName,
            DataTable dataTable,
            DataRowState? rowState = null,
            IEnumerable<DbField> dbFields = null,
            IEnumerable<BulkInsertMapItem> mappings = null,
            SqlBulkCopyOptions options = default,
            string hints = null,
            int? bulkCopyTimeout = null,
            int? batchSize = null,
            bool? isReturnIdentity = null,
            bool? usePhysicalPseudoTempTable = null,
            SqlTransaction transaction = null,
            CancellationToken cancellationToken = default)
        {
            // Validate
            if (dataTable?.Rows.Count <= 0)
            {
                return default;
            }

            // Variables needed
            var dbSetting = connection.GetDbSetting();
            var hasTransaction = transaction != null;

            // Check the transaction
            if (transaction == null)
            {
                // Add the transaction if not present
                transaction = (SqlTransaction)(await connection.EnsureOpenAsync(cancellationToken)).BeginTransaction();
            }
            else
            {
                // Validate the objects
                ValidateTransactionConnectionObject(connection, transaction);
            }

            try
            {
                // Get the DB Fields
                dbFields ??= await DbFieldCache.GetAsync(connection, tableName, transaction, true, cancellationToken);

                // Variables needed
                var identityDbField = dbFields?.FirstOrDefault(dbField => dbField.IsIdentity);
                var tableFields = GetDataColumns(dataTable)
                    .Select(column => column.ColumnName);
                var fields = dbFields?.Select(dbField => dbField.AsField());

                // Filter the fields (based on mappings)
                if (mappings?.Any() == true)
                {
                    fields = fields?
                        .Where(e =>
                            mappings.Any(mapping => string.Equals(mapping.DestinationColumn, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                }
                else
                {
                    // Filter the fields (based on the data table)
                    if (tableFields?.Any() == true)
                    {
                        fields = fields?
                            .Where(e =>
                                tableFields.Any(fieldName => string.Equals(fieldName, e.Name, StringComparison.OrdinalIgnoreCase)) == true);
                    }

                    // Explicitly define the mappings
                    mappings = fields?
                        .Select(e =>
                            new BulkInsertMapItem(e.Name, e.Name));
                }

                // Throw an error if there are no fields
                if (fields?.Any() != true)
                {
                    throw new MissingFieldException("There are no field(s) found for this operation.");
                }

                // Pseudo temp table
                var withPseudoExecution = isReturnIdentity == true && identityDbField != null;
                var tempTableName = await CreateTempTableIfNecessaryAsync(connection, 
                    tableName,
                    usePhysicalPseudoTempTable,
                    transaction,
                    withPseudoExecution,
                    dbSetting,
                    fields,
                    cancellationToken);

                // WriteToServer
                var result = await WriteToServerAsyncInternal(connection,
                    (tempTableName ?? tableName),
                    dataTable,
                    rowState,
                    mappings,
                    options,
                    bulkCopyTimeout,
                    batchSize,
                    withPseudoExecution,
                    transaction,
                    cancellationToken);

                // Check if this is with pseudo
                if (withPseudoExecution)
                {
                    if (isReturnIdentity == true)
                    {
                        var sql = GetBulkInsertSqlText(tableName,
                            tempTableName,
                            fields,
                            identityDbField?.AsField(),
                            hints,
                            dbSetting,
                            withPseudoExecution);

                        // Identify the column
                        var column = dataTable.Columns[identityDbField.Name];
                        if (column?.ReadOnly == false)
                        {
                            using var reader = (DbDataReader)await connection.ExecuteReaderAsync(sql, commandTimeout: bulkCopyTimeout, transaction: transaction, cancellationToken: cancellationToken);

                            result = await SetIdentityForEntitiesAsync(dataTable, reader, column, cancellationToken);
                        }
                        else
                        {
                            result = await connection.ExecuteNonQueryAsync(sql, commandTimeout: bulkCopyTimeout, transaction: transaction, cancellationToken: cancellationToken);
                        }

                        // Drop the table after used
                        sql = GetDropTemporaryTableSqlText(tempTableName, dbSetting);
                        await connection.ExecuteNonQueryAsync(sql, transaction: transaction, cancellationToken: cancellationToken);
                    }
                }

                // Commit the transaction
                if (hasTransaction == false)
                {
                    transaction.Commit();
                }

                // Return the result
                return result;
            }
            catch
            {
                // Rollback the transaction
                if (hasTransaction == false)
                {
                    transaction.Rollback();
                }

                // Throw
                throw;
            }
            finally
            {
                // Dispose the transaction
                if (hasTransaction == false)
                {
                    transaction.Dispose();
                }
            }
        }

        #endregion
        
        private static string CreateTempTableIfNecessary<TSqlTransaction>(
            IDbConnection connection,
            string tableName,
            bool? usePhysicalPseudoTempTable,
            TSqlTransaction transaction,
            bool withPseudoExecution,
            IDbSetting dbSetting,
            IEnumerable<Field> fields)
            where TSqlTransaction : DbTransaction
        {
            if (withPseudoExecution == false) 
                return null;

            var tempTableName = CreateTempTableName(tableName, usePhysicalPseudoTempTable, dbSetting);
            var sql = GetCreateTemporaryTableSqlText(tableName, tempTableName, fields, dbSetting, true);

            connection.ExecuteNonQuery(sql, transaction: transaction);

            return tempTableName;
        }

        private static async Task<string> CreateTempTableIfNecessaryAsync<TSqlTransaction>(IDbConnection connection,
            string tableName,
            bool? usePhysicalPseudoTempTable,
            TSqlTransaction transaction,
            bool withPseudoExecution,
            IDbSetting dbSetting,
            IEnumerable<Field> fields, 
            CancellationToken cancellationToken)
            where TSqlTransaction : DbTransaction
        {
            if (withPseudoExecution == false) 
                return null;

            var tempTableName = CreateTempTableName(tableName, usePhysicalPseudoTempTable, dbSetting);
            var sql = GetCreateTemporaryTableSqlText(tableName, tempTableName, fields, dbSetting, true);

            await connection.ExecuteNonQueryAsync(sql, transaction: transaction, cancellationToken: cancellationToken);

            return tempTableName;
        }
        
        private static string CreateTempTableName(string tableName, bool? usePhysicalPseudoTempTable, IDbSetting dbSetting)
        {
            // Must be fixed name so the RepoDb.Core caches will not be bloated
            var tempTableName = string.Concat("_RepoDb_BulkInsert_", GetTableName(tableName, dbSetting));

            // Add a # prefix if not physical
            if (usePhysicalPseudoTempTable != true) 
                tempTableName = string.Concat("#", tempTableName);
            
            return tempTableName;
        }
    }
}
