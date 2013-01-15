/*
 Limaki 
 
 Author: Lytico
 Copyright (C) 2009-2012 Lytico
 
 http://www.limada.org

 Permission is hereby granted, free of charge, to any person obtaining a copy
 of this software and associated documentation files (the "Software"), to deal
 in the Software without restriction, including without limitation the rights
 to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 copies of the Software, and to permit persons to whom the Software is
 furnished to do so, subject to the following conditions:
 
 The above copyright notice and this permission notice shall be included in
 all copies or substantial portions of the Software.
 
 THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 THE SOFTWARE.
  
 */

using System;
using System.Collections.Generic;
using System.Data.Linq.Mapping;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using DbLinq.Data.Linq.Database.Implementation;
using DbLinq.Data.Linq.Sql;
using DbLinq.Data.Linq.Sugar.Expressions;
using DbLinq.Util;
using DbLinq.Data.Linq;
using DbLinq.Data.Linq.Database;
using System.Data;
using System.Diagnostics;

namespace DbLinq.Util {
    /// <summary>
    /// remark: this is in some parts a copy of
    /// QueryBuilder.Upsert.cs (DbLinq.Data.Linq.Sugar.Implementation)
    /// TODO: open QueryBuilder and make use it
    /// </summary>
    public class UpsertBuilder {
        public class UpsertParameters {
            public MetaTable Table;
            public readonly IList<ObjectInputParameterExpression> InputParameters = new List<ObjectInputParameterExpression>();
            public readonly IList<ObjectOutputParameterExpression> OutputParameters = new List<ObjectOutputParameterExpression>();
            public readonly IList<ObjectInputParameterExpression> PKParameters = new List<ObjectInputParameterExpression>();
            public readonly IList<SqlStatement> InputColumns = new List<SqlStatement>();
            public readonly IList<SqlStatement> InputValues = new List<SqlStatement>();
            public readonly IList<SqlStatement> OutputColumns = new List<SqlStatement>();
            public readonly IList<SqlStatement> OutputValues = new List<SqlStatement>();
            public readonly IList<SqlStatement> OutputExpressions = new List<SqlStatement>();
            public readonly IList<SqlStatement> AutoPKColumns = new List<SqlStatement>();
            public readonly IList<SqlStatement> InputPKColumns = new List<SqlStatement>();
            public readonly IList<SqlStatement> InputPKValues = new List<SqlStatement>();
            public readonly IList<SqlStatement> PKColumns = new List<SqlStatement>();
            public readonly IList<SqlStatement> PKValues = new List<SqlStatement>();
        }

        public class BulkQuery {
            public DataContext DataContext { get; set; }
            public SqlStatement Sql { get; set; }
            public IList<ObjectInputParameterExpression> PrimaryKeyParameters { get; set; }
            public IList<ObjectInputParameterExpression> InputParameters { get; set; }

            protected ITransactionalCommand GetCommand(bool createTransaction) {
                return new TransactionalCommand(Sql.ToString(), createTransaction, DataContext);
            }

            public void CreateParameters(IDbCommand command) {
                if(InputParameters!=null)
                foreach (var inputParameter in InputParameters) {
                    var dbParameter = command.CreateParameter();
                    dbParameter.ParameterName = DataContext.Vendor.SqlProvider.GetParameterName(inputParameter.Alias);
                    command.Parameters.Add(dbParameter);
                }
            }

            public void SetParameterValues(object entity, IDbCommand command) {
                if (InputParameters != null)
                    foreach (var inputParameter in InputParameters) {
                        string name = null;
                        object value = null;
                        try {
                            name = DataContext.Vendor.SqlProvider.GetParameterName(inputParameter.Alias);
                            var dbParameter = command.Parameters[name] as IDbDataParameter;
                            value = NormalizeDbType(inputParameter.GetValue(entity));
                            dbParameter.SetValue(value, inputParameter.ValueType);
                        } catch(Exception ex) {
                            throw new ArgumentException(string.Format("Error setting Parameter {0} = {1}",name,value), ex);
                        }
                    }
            }

            private object NormalizeDbType(object value) {
                System.Data.Linq.Binary b = value as System.Data.Linq.Binary;
                // Mono 2.4.2.3's Binary.operator!= is bad; avoid it.
                if (!object.ReferenceEquals(b, null))
                    return b.ToArray();
                return value;
            }

            public ITransactionalCommand GetCommandTransactional(bool createTransaction) {
                ITransactionalCommand command = this.GetCommand(createTransaction);
                CreateParameters(command.Command);
                return command;
            }
        }
        
        public void WriteLog(DataContext dataContext, string message) {
            if (dataContext.Log != null)
                dataContext.Log.WriteLine(message);
            else
                Trace.WriteLine(message);
        }

        protected IThreadSafeDictionary<Type, BulkQuery> ExistsQueryCache = new ThreadSafeDictionary<Type, BulkQuery>();

        protected string ParametersToString<T>(ITransactionalCommand command) {
            var msg = "<" + typeof(T).Name + "> \t";
            foreach (var p in command.Command.Parameters.OfType<IDataParameter>())
                msg += p.ParameterName+" = "+ (p.Value ?? "<null>")+",\t";
            return msg;
        }

        /// <summary>
        /// Creates a query for insertion
        /// </summary>
        /// <param name="objectToInsert"></param>
        /// <param name="queryContext"></param>
        /// <returns></returns>
        public BulkQuery GetInsertQuery<T>(DataContext dataContext) {
            // TODO: cache
            var upsertParameters = GetUpsertParameters<T>(false, null, dataContext);

            var sqlProvider = dataContext.Vendor.SqlProvider;

            var insertSql = sqlProvider.GetInsert(
                sqlProvider.GetTable(upsertParameters.Table.TableName),
                upsertParameters.InputColumns,
                upsertParameters.InputValues);

            var insertIdSql = sqlProvider.GetInsertIds(
                sqlProvider.GetTable(upsertParameters.Table.TableName),
                upsertParameters.AutoPKColumns,
                upsertParameters.PKColumns,
                upsertParameters.PKValues,
                upsertParameters.OutputColumns,
                upsertParameters.OutputValues,
                upsertParameters.OutputExpressions);

            return new BulkQuery {
                                     DataContext = dataContext,
                                     Sql = insertSql,
                                     InputParameters = upsertParameters.InputParameters,
                                     PrimaryKeyParameters = upsertParameters.PKParameters
                                 };

        }

        protected enum ParameterType {
            Input,
            InputPK,
            Output,
            AutoSync
        }

        /// <summary>
        /// Gets values for insert/update
        /// </summary>
        protected virtual UpsertParameters GetUpsertParameters<T>(bool update, DataContext dataContext) {
            return GetUpsertParameters<T>(update, Columns<T>(), dataContext);
        }

        protected virtual UpsertParameters GetUpsertParameters<T>(bool update, ICollection<MemberInfo> modifiedMembers, DataContext dataContext) {
            var rowType = typeof(T);
            var sqlProvider = dataContext.Vendor.SqlProvider;
            var upsertParameters = new UpsertParameters {
                                                            Table = dataContext.Mapping.GetTable(rowType)
                                                        };
            foreach (var dataMember in upsertParameters.Table.RowType.PersistentDataMembers) {
                var column = sqlProvider.GetColumn(dataMember.MappedName);
                ParameterType type = GetParameterType(dataMember, update, update ? AutoSync.OnUpdate : AutoSync.OnInsert);
                var memberInfo = dataMember.Member;
                // if the column is generated AND not specified, we may have:
                // - an explicit generation (Expression property is not null, so we add the column)
                // - an implicit generation (Expression property is null
                // in all cases, we want to get the value back

                var getter = (Expression<Func<object, object>>)(o => memberInfo.GetMemberValue(o));
                var inputParameter = new ObjectInputParameterExpression(
                    getter,
                    memberInfo.GetMemberType(), dataMember);
                if (dataMember.IsPrimaryKey && (!dataMember.IsDbGenerated)) {
                    upsertParameters.PKColumns.Add(column);
                    upsertParameters.PKParameters.Add(inputParameter);
                    upsertParameters.PKValues.Add(sqlProvider.GetParameterName(inputParameter.Alias));
                }

                if (type == ParameterType.Output) {
                    if (dataMember.Expression != null) {
                        upsertParameters.InputColumns.Add(column);
                        upsertParameters.InputValues.Add(dataMember.Expression);
                    }
                    var setter = (Expression<Action<object, object>>)((o, v) => memberInfo.SetMemberValue(o, v));
                    var outputParameter = new ObjectOutputParameterExpression(setter,
                                                                              memberInfo.GetMemberType(),
                                                                              dataMember.Name);

                    if ((dataMember.IsPrimaryKey) && (dataMember.IsDbGenerated))
                        upsertParameters.AutoPKColumns.Add(column);

                    upsertParameters.OutputColumns.Add(column);
                    upsertParameters.OutputParameters.Add(outputParameter);
                    upsertParameters.OutputValues.Add(sqlProvider.GetParameterName(outputParameter.Alias));
                    upsertParameters.OutputExpressions.Add(dataMember.Expression);
                } else // standard column
                {
                    var isModified = modifiedMembers != null && modifiedMembers.Contains(memberInfo);
                    if (type == ParameterType.InputPK) {
                        upsertParameters.InputPKColumns.Add(column);
                        upsertParameters.InputPKValues.Add(sqlProvider.GetParameterName(inputParameter.Alias));
                        if (isModified) {
                            // stupid! PK is modified, so we will never find the column
                            //upsertParameters.InputColumns.Add(column);
                            //upsertParameters.InputValues.Add(sqlProvider.GetParameterName(inputParameter.Alias+"Old"));
                            upsertParameters.InputParameters.Add(inputParameter);
                        } else {
                            upsertParameters.InputParameters.Add(inputParameter);
                        }
                    }
                        // for a standard column, we keep it only if modifiedMembers contains the specified memberInfo
                        // caution: this makes the cache harder to maintain
                    else if (modifiedMembers == null || isModified) {
                        upsertParameters.InputColumns.Add(column);
                        upsertParameters.InputValues.Add(sqlProvider.GetParameterName(inputParameter.Alias));
                        upsertParameters.InputParameters.Add(inputParameter);
                    }

                    if (type == ParameterType.AutoSync) {
                        var setter = (Expression<Action<object, object>>)((o, v) => memberInfo.SetMemberValue(o, v));
                        var outputParameter = new ObjectOutputParameterExpression(setter,
                                                                                  memberInfo.GetMemberType(),
                                                                                  dataMember.Name);
                        upsertParameters.OutputColumns.Add(column);
                        upsertParameters.OutputParameters.Add(outputParameter);
                        upsertParameters.OutputValues.Add(sqlProvider.GetParameterName(outputParameter.Alias));
                        upsertParameters.OutputExpressions.Add(dataMember.Expression);
                    }
                }
            }
            return upsertParameters;
        }

        /// <summary>
        /// Provides the parameter type for a given data member
        /// </summary>
        /// <param name="objectToUpsert"></param>
        /// <param name="dataMember"></param>
        /// <param name="update"></param>
        /// <returns></returns>
        protected virtual ParameterType GetParameterType(MetaDataMember dataMember, bool update, AutoSync autoSync) {
            var memberInfo = dataMember.Member;
            // the deal with columns is:
            // PK only:  criterion for INSERT, criterion for UPDATE
            // PK+GEN:   implicit/criterion for INSERT, criterion for UPDATE
            // GEN only: implicit for both
            // -:        explicit for both
            //
            // explicit is input,
            // implicit is output, 
            // criterion is input PK
            ParameterType type;
            if (dataMember.IsPrimaryKey) {
                if (update)
                    type = ParameterType.InputPK;
                else {
                    if (dataMember.IsDbGenerated) {
                        type = ParameterType.Output;
                    } else
                        type = ParameterType.Input;
                }
            } else {
                if (dataMember.IsDbGenerated)
                    type = ParameterType.Output;
                else if ((dataMember.AutoSync == AutoSync.Always) || (dataMember.AutoSync == autoSync))
                    type = ParameterType.AutoSync;
                else
                    type = ParameterType.Input;
            }
            return type;
        }

        /// <summary>
        /// Determines if a property is different from its default value
        /// </summary>
        /// <param name="target"></param>
        /// <param name="memberInfo"></param>
        /// <returns></returns>
        protected virtual bool IsSpecified(object target, MemberInfo memberInfo) {
            object value = memberInfo.GetMemberValue(target);
            if (value == null)
                return false;
            if (Equals(value, TypeConvert.GetDefault(memberInfo.GetMemberType())))
                return false;
            return true;
        }

        /// <summary>
        /// Creates or gets an UPDATE query
        /// </summary>
        /// <param name="objectToUpdate"></param>
        /// <param name="modifiedMembers">List of modified members, or NULL</param>
        /// <param name="queryContext"></param>
        /// <returns></returns>
        public BulkQuery GetUpdateQuery<T>(DataContext dataContext) {
            return GetUpdateQuery<T>(Columns<T>(), dataContext);
        }

        public BulkQuery GetUpdateQuery<T>(ICollection<MemberInfo> modifiedMembers, DataContext dataContext) {
            var upsertParameters = GetUpsertParameters<T>(true, modifiedMembers, dataContext);
            var sqlProvider = dataContext.Vendor.SqlProvider;
            var updateSql = sqlProvider.GetUpdate(
                sqlProvider.GetTable(upsertParameters.Table.TableName),
                upsertParameters.InputColumns, upsertParameters.InputValues,
                upsertParameters.OutputValues, upsertParameters.OutputExpressions,
                upsertParameters.InputPKColumns, upsertParameters.InputPKValues
                );
            var insertIdSql = (
                                  upsertParameters.OutputValues.Count == 0)
                                  ? ""
                                  : sqlProvider.GetInsertIds(
                                      sqlProvider.GetTable(upsertParameters.Table.TableName),
                                      upsertParameters.AutoPKColumns,
                                      upsertParameters.PKColumns,
                                      upsertParameters.PKValues,
                                      upsertParameters.OutputColumns,
                                      upsertParameters.OutputValues,
                                      upsertParameters.OutputExpressions);
            return new BulkQuery {
                DataContext = dataContext,
                Sql = updateSql,
                InputParameters = upsertParameters.InputParameters,
                PrimaryKeyParameters = upsertParameters.PKParameters
            };
        }

        public BulkQuery GetExistsQuery<T>(DataContext dataContext) {
            BulkQuery result = null;

            if (ExistsQueryCache.TryGetValue(typeof(T), out result)) {
                if (result.DataContext == dataContext)
                    return result;
            }

            var upsertParameters = GetUpsertParameters<T>(true, Columns<T>(), dataContext);
            var sqlProvider = dataContext.Vendor.SqlProvider;
            var sqlBuilder = new SqlStatementBuilder("SELECT COUNT (*) FROM ");
            sqlBuilder.Append(sqlProvider.GetTable(upsertParameters.Table.TableName));
            sqlBuilder.Append(" WHERE ");
            var valueSet = false;
            for (IEnumerator<SqlStatement> column = upsertParameters.InputPKColumns.GetEnumerator(), value = upsertParameters.InputPKValues.GetEnumerator(); column.MoveNext() && value.MoveNext(); ) {
                if (valueSet)
                    sqlBuilder.Append(" AND ");
                sqlBuilder.AppendFormat("{0} = {1}", column.Current, value.Current);
                valueSet = true;
            }

            result =  new BulkQuery {
                DataContext = dataContext,
                Sql = sqlBuilder.ToSqlStatement(),
                InputParameters = upsertParameters.PKParameters,
                PrimaryKeyParameters = upsertParameters.PKParameters
            };
            ExistsQueryCache[typeof (T)] = result;
            return result;
        }

        /// <summary>
        /// Creates or gets a DELETE query
        /// </summary>
        /// <param name="objectToDelete"></param>
        /// <param name="queryContext"></param>
        /// <returns></returns>
        public BulkQuery GetDeleteQuery<T>(DataContext dataContext) {
            var sqlProvider = dataContext.Vendor.SqlProvider;
            var rowType = typeof(T);
            var table = dataContext.Mapping.GetTable(rowType);
            var deleteParameters = new List<ObjectInputParameterExpression>();
            var pkColumns = new List<SqlStatement>();
            var pkValues = new List<SqlStatement>();
            foreach (var pkMember in table.RowType.IdentityMembers) {
                var memberInfo = pkMember.Member;
                var getter = (Expression<Func<object, object>>)(o => memberInfo.GetMemberValue(o));
                var inputParameter = new ObjectInputParameterExpression(
                    getter,
                    memberInfo.GetMemberType(), pkMember);
                var column = sqlProvider.GetColumn(pkMember.MappedName);
                pkColumns.Add(column);
                pkValues.Add(sqlProvider.GetParameterName(inputParameter.Alias));
                deleteParameters.Add(inputParameter);
            }
            var deleteSql = sqlProvider.GetDelete(sqlProvider.GetTable(table.TableName), pkColumns, pkValues);
            return new BulkQuery { DataContext = dataContext, Sql = deleteSql, InputParameters = deleteParameters };
        }
        public BulkQuery GetDeleteAllQuery<T>(DataContext dataContext) {
            var sqlProvider = dataContext.Vendor.SqlProvider;
            var rowType = typeof(T);
            var table = dataContext.Mapping.GetTable(rowType);

            var deleteBuilder = new SqlStatementBuilder("DELETE FROM ");
            deleteBuilder.Append(sqlProvider.GetTable(table.TableName));
            return new BulkQuery { DataContext = dataContext, Sql = deleteBuilder.ToSqlStatement()};
        }
        
        protected IDictionary<Type, ICollection<MemberInfo>> _columnsCache = new Dictionary<Type, ICollection<MemberInfo>>();
        public ICollection<MemberInfo> Columns<T>() {
            ICollection<MemberInfo> result = null;
            var type = typeof(T);
            if (!_columnsCache.TryGetValue(type, out result)) {
                result = type.GetMembers().Where(p => p.IsDefined(typeof(ColumnAttribute), true)).ToArray();
                _columnsCache.Add(type, result);
            }
            return result;
        }

        public void DeleteAll<T>(DataContext dataContext) {
            WriteLog(dataContext, "** DeleteAll<" + typeof(T).Name + ">");
            var query = GetDeleteAllQuery<T>(dataContext);
            var result = 0;
            var command = query.GetCommandTransactional(true);
            WriteLog(dataContext, command.Command.CommandText);
            command.Command.ExecuteNonQuery();
            Commit(command);
            WriteLog(dataContext, string.Format("deleted all from\t{0}", typeof(T).Name));

        }

        public virtual int BulkDelete<T>(DataContext dataContext, IEnumerable<T> entities) {
            WriteLog(dataContext, "** BulkDelete<" + typeof(T).Name + ">");
            var query = GetDeleteQuery<T>(dataContext);
            var result = Bulk(dataContext, entities, query);
            WriteLog(dataContext, string.Format("deleted\t{0}\titems", result));
            return result;
        }

        public virtual int BulkUpdate<T>(DataContext dataContext, IEnumerable<T> entities) {
            WriteLog(dataContext, "** BulkUpdate<" + typeof(T).Name + ">");

            var query = GetUpdateQuery<T>(dataContext);

            // entity consists only of keys; there can be no update with this pattern
            // todo: cache this and reuse it in BulkUpsert
            if (query.PrimaryKeyParameters.SequenceEqual(query.InputParameters)) {
                WriteLog(dataContext, "WARNING: no update possible for this entity");
                return 0;
            }

            var result = Bulk(dataContext, entities, query);

            WriteLog(dataContext, string.Format("updated\t{0}\titems", result));
            return result;
        }

        public virtual int BulkInsert<T>(DataContext dataContext, IEnumerable<T> entities) {
            WriteLog(dataContext, "** BulkInsert<" + typeof(T).Name + ">");
            var query = GetInsertQuery<T>(dataContext);
            var result = Bulk(dataContext, entities, query);
            WriteLog(dataContext, string.Format("inserted\t{0}\titems", result));
            return result;
        }

        public virtual int Bulk<T>(DataContext dataContext, IEnumerable<T> entities, BulkQuery query) {
            var result = 0;

            var command = query.GetCommandTransactional(true);
            WriteLog(dataContext, command.Command.CommandText);
            foreach (var item in entities) {
                try {
                    query.SetParameterValues(item, command.Command);
                    command.Command.ExecuteNonQuery();
                    result++;
                } catch (Exception ex) {
                    var caller = new System.Diagnostics.StackTrace().GetFrame(1).GetMethod().Name;
                    WriteLog(dataContext, string.Format("Error in {0}:\t {1}", caller, ParametersToString<T>(command)));
                    throw ex;
                }
            }
            Commit(command);

            return result;
        }

        public bool CommitLocal { get; set; }
        protected void Commit(ITransactionalCommand command) {
            if (CommitLocal)
                try {
                    command.Commit();
                    Trace.Write(".C.");
                } catch (Exception ex) {
                    command.Command.Transaction.Rollback();
                    throw ex;
                } finally {
                    command.Dispose();
                }
        }

        public virtual bool Contains<T>(DataContext dataContext, T item) {
            var query = GetExistsQuery<T>(dataContext);
            var command = query.GetCommandTransactional(false);
            return Exists<T>(item, query, command)!=0;
        }

        protected virtual int Exists<T>(T item,BulkQuery query,ITransactionalCommand command) {
            query.SetParameterValues(item, command.Command);
            var o = command.Command.ExecuteScalar();
            var i = 0;
            if (o is Decimal)
                i = (int)(Decimal)o;
            else if (o is int)
                i = (int)o;
            else
                throw new Exception(string.Format("unknown result of Command.ExecuteScalar:\t{0}:\t{1}", o == null ? "<null>" : o.GetType().FullName, o));
            return i;

        }
        public virtual int BulkUpsert<T>(DataContext dataContext, IEnumerable<T> entities) 
            where T:class {
            WriteLog(dataContext, "** BulkUpsert<" + typeof (T).Name + ">");
            // TODO: check if entity consists of keys only; see:BulkUpdate
            var result = 0;
            var updates = new List<T>();
            var inserts = new List<T>();
            var query = GetExistsQuery<T>(dataContext);
            var command = query.GetCommandTransactional(false);
            
            WriteLog(dataContext, command.Command.CommandText);

            foreach (var item in entities) {
                var i = Exists<T>(item, query, command);
                if (i != 0)
                    updates.Add(item);
                else
                    inserts.Add(item);
            }
            
            command.Dispose();

            if (inserts.Count > 0)
                result += BulkInsert(dataContext, inserts);

            if (updates.Count > 0)
                result += BulkUpdate(dataContext, updates);

            WriteLog(dataContext, string.Format("upserted\t{0}\titems",result));
            return result;

        }
    }
}