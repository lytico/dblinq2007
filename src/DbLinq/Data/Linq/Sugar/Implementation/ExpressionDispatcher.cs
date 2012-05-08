#region MIT license
// 
// MIT license
//
// Copyright (c) 2007-2008 Jiri Moudry, Pascal Craponne
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
// 
#endregion

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

using DbLinq.Data.Linq.Mapping;
using DbLinq.Data.Linq.Sugar;
using DbLinq.Data.Linq.Sugar.ExpressionMutator;
using DbLinq.Data.Linq.Sugar.Expressions;
using DbLinq.Data.Linq.Sugar.Implementation;
using DbLinq.Factory;
using DbLinq.Util;
using System.Collections;
using System.Data.Linq.Mapping;


namespace DbLinq.Data.Linq.Sugar.Implementation
{
    internal partial class ExpressionDispatcher : IExpressionDispatcher
    {
        public IExpressionQualifier ExpressionQualifier { get; set; }
        public IDataRecordReader DataRecordReader { get; set; }
        public IDataMapper DataMapper { get; set; }

        public ExpressionDispatcher()
        {
            ExpressionQualifier = ObjectFactory.Get<IExpressionQualifier>();
            DataRecordReader = ObjectFactory.Get<IDataRecordReader>();
            DataMapper = ObjectFactory.Get<IDataMapper>();
        }

        /// <summary>
        /// Registers the first table. Extracts the table type and registeres the piece
        /// </summary>
        /// <param name="requestingExpression"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        public virtual Expression CreateTableExpression(Expression requestingExpression, BuilderContext builderContext)
        {
            var callExpression = (MethodCallExpression)requestingExpression;
            var requestingType = callExpression.Arguments[0].Type;
            return CreateTable(GetQueriedType(requestingType), builderContext);
        }

        /// <summary>
        /// Registers the first table. Extracts the table type and registeres the piece
        /// </summary>
        /// <param name="requestingExpression"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        public virtual Expression GetTable(Expression requestingExpression, BuilderContext builderContext)
        {
            var callExpression = (MethodCallExpression)requestingExpression;
            var requestingType = callExpression.Arguments[0].Type;
            return CreateTable(GetQueriedType(requestingType), builderContext);
        }

        /// <summary>
        /// Builds the upper select clause
        /// </summary>
        /// <param name="selectExpression"></param>
        /// <param name="builderContext"></param>
        public virtual void BuildSelect(Expression selectExpression, BuilderContext builderContext)
        {
            // collect columns, split Expression in
            // - things we will do in CLR
            // - things we will do in SQL
            LambdaExpression lambdaSelectExpression;
            // if we have a GroupByExpression, the result type is not the same:
            // - we need to read what is going to be the Key expression
            // - the final row generator builds a IGrouping<K,T> instead of T
            if (selectExpression is TableExpression)
            {
                //Expression group;
                selectExpression = GetTableGroup(selectExpression, builderContext) ?? selectExpression;
            }
            var selectGroupExpression = selectExpression as GroupExpression;
            if (selectGroupExpression != null)
            {
                //Removing group from the currentSQl (actually, if the group is the last clause, there's no group by clause on the sql
                builderContext.CurrentSelect.Group.Remove(selectGroupExpression);
                lambdaSelectExpression = GetGroupReader(selectGroupExpression, builderContext);
            }
            else
                lambdaSelectExpression = CutOutOperands(selectExpression, builderContext, true);
            // look for tables and use columns instead
            // (this is done after cut, because the part that went to SQL must not be converted)
            //selectExpression = selectExpression.Recurse(e => CheckTableExpression(e, builderContext));
            // the last return value becomes the select, with CurrentScope
            builderContext.CurrentSelect.Reader = lambdaSelectExpression;
        }

        private static Expression GetTableGroup(Expression selectExpression, BuilderContext builderContext)
        {
            Expression group;
            group = builderContext.CurrentSelect.Group.LastOrDefault(w => w.GroupedExpression.Equals(selectExpression));
            if (group != null)
                selectExpression = group;
            return group;
        }

        private LambdaExpression GetGroupReader(GroupExpression selectGroupExpression, BuilderContext builderContext)
        {
            var dataRecordParameter = Expression.Parameter(typeof(IDataRecord), "dataRecord");
            var mappingContextParameter = Expression.Parameter(typeof(MappingContext), "mappingContext");
            return GetGroupReader(selectGroupExpression, builderContext, dataRecordParameter, mappingContextParameter);
        }

        private LambdaExpression GetGroupReader(GroupExpression selectGroupExpression, BuilderContext builderContext,
                    ParameterExpression dataRecordParameter, ParameterExpression mappingContextParameter)
        {
            LambdaExpression lambdaSelectExpression, lambdaSelectKeyExpression;
            GetGroupLambdaSelecs(selectGroupExpression, builderContext, out lambdaSelectExpression, out lambdaSelectKeyExpression);
            lambdaSelectExpression = BuildSelectGroup(lambdaSelectExpression, lambdaSelectKeyExpression,
                                                      builderContext);
            return lambdaSelectExpression;
        }


        private void GetGroupLambdaSelecs(GroupExpression group, BuilderContext context,
            out LambdaExpression lambdaSelect, out LambdaExpression lambdaKeySelect)
        {
            lambdaSelect = CutOutOperands(group.GroupedExpression, context);
            lambdaKeySelect = CutOutOperands(group.KeyExpression, context);
        }
        public InvocationExpression getJustIGrouping(GroupExpression selectGroupExpression, BuilderContext builderContext,
                    ParameterExpression dataRecordParameter, ParameterExpression mappingContextParameter)
        {
            LambdaExpression lambdaSelectExpression;
            lambdaSelectExpression = CutOutOperands(selectGroupExpression.GroupedExpression, builderContext);
            var lambdaSelectKeyExpression = CutOutOperands(selectGroupExpression.KeyExpression, builderContext);
            var Lambda = Expression.Lambda(GetIGrouping(lambdaSelectExpression, lambdaSelectKeyExpression, dataRecordParameter, mappingContextParameter),
                dataRecordParameter, mappingContextParameter);
            return Expression.Invoke(Lambda, dataRecordParameter, mappingContextParameter);
        }
        /// <summary>
        /// Builds the lambda as:
        /// (dr, mc) => new ListGroup<K,T>(selectKey(dr,mc),select(dr,mc))
        /// </summary>
        /// <param name="select"></param>
        /// <param name="selectKey"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        protected virtual LambdaExpression BuildSelectGroup(LambdaExpression select, LambdaExpression selectKey,
                                                            BuilderContext builderContext)
        {
            var dataRecordParameter = Expression.Parameter(typeof(IDataRecord), "dataRecord");
            var mappingContextParameter = Expression.Parameter(typeof(MappingContext), "mappingContext");
            return BuildSelectGroup(select, selectKey, builderContext, dataRecordParameter, mappingContextParameter);
        }

        protected virtual LambdaExpression BuildSelectGroup(LambdaExpression select, LambdaExpression selectKey,
                                                            BuilderContext builderContext,
                                                            ParameterExpression dataRecordParameter,
                                                            ParameterExpression mappingContextParameter)
        {
            UnaryExpression newIGrouping = GetIGrouping(select, selectKey, dataRecordParameter, mappingContextParameter);
            var t0 = typeof(IEnumerable<>).MakeGenericType(
                //(newIGrouping.Operand as NewExpression).Arguments[0].Type,
                (newIGrouping.Operand as NewExpression).Arguments[1].Type);
            var t = typeof(IFindAndProcess);
            var p = Expression.Parameter(t, "p");
            //var lambda = Expression.Lambda(newIGrouping, dataRecordParameter, mappingContextParameter, p);
            ParameterExpression pReg = Expression.Parameter(typeof(object), "re");
            var addNew = getGroupingOp(dataRecordParameter, mappingContextParameter, pReg, new List<Expression>() { selectKey, select });
            ParameterExpression pList = Expression.Parameter(t, "re");
            MethodInfo met = t.GetMethod("FindAndProcess");
            Expression Validation = Expression.Call(pList, met,
                                                    Expression.Lambda(addNew[0], pReg),
                                                    Expression.Lambda(addNew[1], pReg));
            Validation = Expression.Invoke(Expression.Lambda(Validation, pList), pList);
            var cond = Expression.Condition(Validation, newIGrouping, Expression.Convert(Expression.Constant(null), newIGrouping.Type));
            var ret = Expression.Lambda(cond, dataRecordParameter, mappingContextParameter, pList);
            return ret;
        }

        private static UnaryExpression GetIGrouping(LambdaExpression select, LambdaExpression selectKey, ParameterExpression dataRecordParameter, ParameterExpression mappingContextParameter)
        {
            var lType = select.Body.Type;
            Type groupingType;
            ConstructorInfo groupingCtor;
            var invokeSelect = Expression.Invoke(select, dataRecordParameter, mappingContextParameter);
            Expression newLineGrouping;
            var kType = selectKey.Body.Type;
            groupingType = typeof(ListGroup<,>).MakeGenericType(kType, lType);
            groupingCtor = groupingType.GetConstructor(new[] { kType, lType });
            var invokeSelectKey = Expression.Invoke(selectKey, dataRecordParameter, mappingContextParameter);
            newLineGrouping = Expression.New(groupingCtor, invokeSelectKey, invokeSelect);
            //return Expression.Convert(newLineGrouping, typeof(IEnumerable<>).MakeGenericType(lType));
            return Expression.Convert(newLineGrouping, typeof(IGrouping<,>).MakeGenericType(kType,lType));
        }

        private static UnaryExpression GetGroupList(GroupExpression group, BuilderContext builderContext,
                                    ParameterExpression dataRecordParameter, ParameterExpression mappingContextParameter)
        {
            var t = typeof(ListGroup<,>);
            return null;
        }

        /// <summary>
        /// Cuts Expressions between CLR and SQL:
        /// - Replaces Expressions moved to SQL by calls to DataRecord values reader
        /// - SQL expressions are placed into Operands
        /// - Return value creator is the returned Expression
        /// </summary>
        /// <param name="selectExpression"></param>
        /// <param name="builderContext"></param>
        protected virtual LambdaExpression CutOutOperands(Expression selectExpression, BuilderContext builderContext, bool parentCall = false)
        {
            var dataRecordParameter = Expression.Parameter(typeof(IDataRecord), "dataRecord");
            var mappingContextParameter = Expression.Parameter(typeof(MappingContext), "mappingContext");
            var expression = CutOutOperands(selectExpression, dataRecordParameter, mappingContextParameter, builderContext);
            //ParentCall is the first call of this method, the call that effectively will have, at this point
            //the final type of the resultset.
            if (parentCall)
            {
                var t = typeof(ListResult<>).MakeGenericType(expression.Type);
                ParameterExpression pList = Expression.Parameter(t, "re");
                var nex = expression as NewExpression;
                if (nex != null)
                {
                    List<Expression> addNew = null;
                    MethodInfo met = t.GetMethod("FindAndProcess");
                    ParameterExpression pReg = Expression.Parameter(typeof(object), "re");
                    for(int i = 0; i < nex.Members.Count; i++)
                    {
                        var m = nex.Members[i];
                        var ma = Expression.MakeMemberAccess(Expression.Convert(pReg, nex.Type), m);
                        if (m.GetMemberType().Implements<IEnumerable>())
                        {
                            List<Expression> newIt;
                            try
                            {
                                newIt = nex.Arguments[i].GetEntityType();
                            }
                            catch { continue; }
                            RemoveGroupOf((newIt[1] as LambdaExpression).Body.Type, builderContext);
                            addNew = getGroupingOp(dataRecordParameter, mappingContextParameter, ma, newIt, addNew);
                        }
                        
                    }
                    if (addNew != null)
                    {
                        Expression Validation = Expression.Call(pList, met,
                                                    Expression.Lambda(addNew[0], pReg),
                                                    Expression.Lambda(addNew[1], pReg));
                        Validation = Expression.Invoke(Expression.Lambda(Validation, pList), pList);
                        expression = Expression.Condition(Validation, expression, Expression.Convert(Expression.Constant(null), expression.Type));
                    }
                }
                return Expression.Lambda(expression, dataRecordParameter, mappingContextParameter, pList);
            }
            return expression == null? null : Expression.Lambda(expression, dataRecordParameter, mappingContextParameter);
        }

        private void RemoveGroupOf(Type expression, BuilderContext builderContext)
        {
            GroupExpression groupRem = null;
            foreach (var group in builderContext.CurrentSelect.Group)
            {
                if (group.Type == expression)
                    groupRem = group;
            }
            if (groupRem != null)
                builderContext.CurrentSelect.Group.Remove(groupRem);
        }

        private static List<Expression> getGroupingOp(ParameterExpression dataRecordParameter, ParameterExpression mappingContextParameter,
                    Expression ma, List<Expression> newIt, List<Expression> addNew = null)
        {

            if (addNew == null)
                addNew = new List<Expression>();
            while (addNew.Count < 2)
                addNew.Add(null);
            var t2 = typeof(ListGroup<,>).MakeGenericType((newIt[0] as LambdaExpression).Body.Type, (newIt[1] as LambdaExpression).Body.Type);
            MethodInfo add = t2.GetMethod("ExpAdd");
            MemberInfo mem = t2.GetSingleMember("Key");
            var cex = Expression.Convert(ma, t2);
            MemberExpression mex = Expression.MakeMemberAccess(cex, mem);
            var comp = GetComparacao(mex, newIt[0], dataRecordParameter, mappingContextParameter);
            var v = Expression.Call(cex, add, Expression.Invoke(newIt[1], dataRecordParameter, mappingContextParameter));
            if (addNew[0] == null)
            {
                addNew[0] = comp;
                addNew[1] = v;
            }
            else
            {
                addNew[0] = Expression.And(addNew[0], comp);
                addNew[1] = Expression.Or(addNew[1], v);
            }
            return addNew;
        }


        public static Expression GetComparacao(Expression original, Expression newValue0, ParameterExpression dataRecordParameter, ParameterExpression mappingContextParameter)
        {
            Expression exp = null;
            //If the expression is a table
            LambdaExpression newValue = newValue0 as LambdaExpression;
            var atb = original.Type.GetAttribute<TableAttribute>();
            if (atb != null)
            {
                exp = null;
                var Columns = original.Type.GetProperties().Where(p => p.GetAttribute<ColumnAttribute>() != null).ToList();
                for (int i = 0; i < Columns.Count; i++)
                {
                    var primary = Columns[i];
                    if (primary.IsPrimary())
                    {
                        var vex = newValue.GetBindvalue(i);
                        var eq = GetComparacao(Expression.MakeMemberAccess(original, primary), vex, dataRecordParameter, mappingContextParameter);
                        if (exp == null)
                            exp = eq;
                        else
                            exp = Expression.And(exp, eq);
                    }
                }
            }
            else if (newValue.Body is NewExpression)
            {
                exp = null;
                var nex = newValue.Body as NewExpression;
                foreach (var m in nex.Members)
                {
                    var lex = Expression.Lambda(Expression.MakeMemberAccess(nex, m), newValue.Parameters.ToArray());
                   var eq = GetComparacao(Expression.MakeMemberAccess(original, m), lex, dataRecordParameter, mappingContextParameter);
                    if (exp == null)
                        exp = eq;
                    else
                        exp = Expression.And(exp, eq);
                }
            }
            else
            {
                exp = Expression.Equal(original, Expression.Invoke(newValue, dataRecordParameter, mappingContextParameter));
            }
            return exp;
        }


        /// <summary>
        /// Cuts tiers in CLR / SQL.
        /// The search for cut is top-down
        /// </summary>
        /// <param name="expression"></param>
        /// <param name="dataRecordParameter"></param>
        /// <param name="mappingContextParameter"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        protected virtual Expression CutOutOperands(Expression expression,
                                                    ParameterExpression dataRecordParameter, ParameterExpression mappingContextParameter,
                                                    BuilderContext builderContext)
        {
            // two options: we cut and return
            if (GetCutOutOperand(expression, builderContext))
            {
                // "cutting out" means we replace the current expression by a SQL result reader
                // before cutting out, we check that we're not cutting a table
                // in this case, we convert it into its declared columns
                if (expression is TableExpression)
                {
                    //if the table was grouped, then it's a implicity group expression, with the into clause
                    return GetOutputTableReader((TableExpression)expression, dataRecordParameter,
                                                mappingContextParameter, builderContext);
             
                }
                // for EntitySets, we have a special EntitySet builder
                if (expression is EntitySetExpression)
                {
                    return GetEntitySetBuilder((EntitySetExpression) expression, dataRecordParameter,
                                               mappingContextParameter, builderContext);
                    // TODO record EntitySet information, so we can initalize it with owner
                }
                // then, the result is registered
                return GetOutputValueReader(expression, dataRecordParameter, mappingContextParameter, builderContext);
            }
            // or we dig down
            var operands = new List<Expression>();
            Dictionary<int, Expression> groupers = new Dictionary<int, Expression>();
            Dictionary<int, Expression> groupeds = new Dictionary<int, Expression>();
            bool isgroup;
            GroupExpression group;
            //Defining if there's a group
            int i = 0;
            foreach (var operand in expression.GetOperands())
            {
                if (isGroupedField(operand, builderContext, out group))
                    groupeds.Add(i, group);
                else
                    groupers.Add(i, operand);
                i++;
            }
            if (groupeds.Count > 0)
            {
                foreach(var k in groupeds.Keys.ToArray())
                {
                 //   groupeds[k] = new GroupExpression((groupeds[k] as GroupExpression).GroupedExpression, groupers.Values.First());
                    //I turned the setter accessible to do this
                    //for while, just one group, until I learn how to do with many
                    //(g as GroupExpression).KeyExpression = groupers.Values.First();
                }
            }
            var e = groupers.Union(groupeds).OrderBy(kv => kv.Key);
            foreach (var kv in e)
            {
                var operand = kv.Value;
                if (operand == null)
                    operands.Add(null);
                else
                {
                    Expression newOp;
                    if (operand is GroupExpression)
                        newOp = getJustIGrouping(operand as GroupExpression, builderContext, dataRecordParameter, mappingContextParameter);
                    else
                        newOp = CutOutOperands(operand, dataRecordParameter, mappingContextParameter, builderContext);
                    operands.Add(newOp);
                }
            }
            return expression.ChangeOperands(operands);
        }

        private bool isGroupedField(Expression expression, BuilderContext builderContext, out GroupExpression group)
        {
            if (expression.Type.Implements<IEnumerable>())
            {
                GroupExpression exp = null;
                try
                {
                    var t = expression.Type.GetGenericArguments().First();
                    exp = builderContext.CurrentSelect.Group.LastOrDefault(g => g.Type.Equals(t));
                }
                catch { }
                group = exp;
                return (exp != null);
            }
            group = null;
            return false;
        }
        /// <summary>
        /// Returns true if we must cut out the given Expression
        /// </summary>
        /// <param name="operand"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        private bool GetCutOutOperand(Expression operand, BuilderContext builderContext)
        {
            bool cutOut = false;
            var tier = ExpressionQualifier.GetTier(operand);
            if ((tier & ExpressionTier.Sql) != 0) // we can cut out only if the following expressiong can go to SQL
            {
                // then we have two possible strategies, load the DB at max, then it's always true from here
                if (builderContext.QueryContext.MaximumDatabaseLoad)
                    cutOut = true;
                else // if no max database load then it's min: we switch to SQL only when CLR doesn't support the Expression
                    cutOut = (tier & ExpressionTier.Clr) == 0;
            }
            return cutOut;
        }

        /// <summary>
        /// Checks any expression for a TableExpression, and eventually replaces it with the convenient columns selection
        /// </summary>
        /// <param name="expression"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        protected virtual Expression CheckTableExpression(Expression expression, BuilderContext builderContext)
        {
            if (expression is TableExpression)
                return GetSelectTableExpression((TableExpression)expression, builderContext);
            return expression;
        }

        /// <summary>
        /// Replaces a table selection by a selection of all mapped columns (ColumnExpressions).
        /// ColumnExpressions will be replaced at a later time by the tier splitter
        /// </summary>
        /// <param name="tableExpression"></param>
        /// <param name="builderContext"></param>
        /// <returns></returns>
        protected virtual Expression GetSelectTableExpression(TableExpression tableExpression, BuilderContext builderContext)
        {
            var bindings = new List<MemberBinding>();
            foreach (var columnExpression in RegisterAllColumns(tableExpression, builderContext))
            {
                var binding = Expression.Bind((MethodInfo) columnExpression.MemberInfo, columnExpression);
                bindings.Add(binding);
            }
            var newExpression = Expression.New(tableExpression.Type);
            return Expression.MemberInit(newExpression, bindings);
        }

        /// <summary>
        /// Returns a queried type from a given expression, or null if no type can be found
        /// </summary>
        /// <param name="expression"></param>
        /// <returns></returns>
        public virtual Type GetQueriedType(Expression expression)
        {
            return GetQueriedType(expression.Type);
        }

        /// <summary>
        /// Extracts the type from the potentially generic type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public virtual Type GetQueriedType(Type type)
        {
            if (typeof(IQueryable).IsAssignableFrom(type))
            {
                if (type.IsGenericType)
                    return type.GetGenericArguments()[0];
            }
            return null;
        }

        /// <summary>
        /// Returns the parameter name, if the Expression is a ParameterExpression, null otherwise
        /// </summary>
        /// <param name="expression"></param>
        /// <returns></returns>
        public virtual string GetParameterName(Expression expression)
        {
            if (expression is ParameterExpression)
                return ((ParameterExpression)expression).Name;
            return null;
        }

        /// <summary>
        /// Merges a parameter and a parameter list
        /// </summary>
        /// <param name="p1"></param>
        /// <param name="p2"></param>
        /// <returns></returns>
        public virtual IList<Expression> MergeParameters(Expression p1, IEnumerable<Expression> p2)
        {
            var p = new List<Expression>();
            p.Add(p1);
            p.AddRange(p2);
            return p;
        }
    }
}
