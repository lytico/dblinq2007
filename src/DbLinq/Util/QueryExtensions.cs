using System.Data;
using System.Linq;
using DbLinq.Data.Linq.Implementation;
using DbLinq.Data.Linq.Sugar;
namespace DbLinq.Util {
    public static class QueryExtensions {
        static SelectQuery GetQuery<T>(IQueryable<T> query) {
             var provider = query.Provider as QueryProvider<T>;
             if (provider != null) {
                 return provider.GetQuery(null);
             }
             return null;
        }

        public static IDbCommand GetCommand<T>(IQueryable<T> query) {
            var select = GetQuery(query);
            if (select != null) {
                return select.GetCommand().Command;
            }

            return null;
        }

        public static long DbCount<T>(IQueryable<T> query) {
            var selectQuery = GetQuery(query);
            if (selectQuery != null) {
                var log = selectQuery.DataContext.Log;
                var command = selectQuery.GetCommand().Command;
                if (command != null) {
                    command.CommandText = "select count(*) from (" +"\r\n"+
                                          command.CommandText +"\r\n"+
                                          ")";

                    selectQuery.DataContext.WriteLog(command);
                    var result = command.ExecuteScalar();
                    if (result is long)
                        return (long)result;
                    if (result is decimal)
                        return (long)(decimal)result;
                }
            }

            return query.Count();
        }
    }
}