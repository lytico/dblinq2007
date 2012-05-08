using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;

namespace DbLinq.Util
{
    public static class MethodExtensions
    {
        public static object MakeGenericCall(this MethodInfo method, object _this, Type[] genArgs, params object[] parameters)
        {
            var m = method.MakeGenericMethod(genArgs);
            return m.Invoke(_this, parameters);
        }
    }
}
