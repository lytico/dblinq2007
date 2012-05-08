using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using DbLinq.Data.Linq.Mapping;

namespace DbLinq.Data.Linq.Sugar.Implementation
{

    internal class ListGroup<K, T> : List<T>, IGrouping<K, T>
    {
        public K Key { get; private set; }
        public T line { get; private set; }

        public ListGroup(K key, T line)
        {
            Key = key;
            Add(line);
        }

        public bool ExpAdd(T line)
        {
            Add(line);
            return true;
        }
    }

    internal interface IFindAndProcess
    {
        bool FindAndProcess(Func<object, bool> find, Func<object, bool> process);
    }

    internal class ListResult<T> : List<T>, IFindAndProcess
    {
        //public bool FindAndProcess(Func<T, bool> find, Func<T,bool> process)
        //{
        //    var r = this.FirstOrDefault(find);
        //    if (r != null)
        //        return !process(r);
        //    return true;
        //}

        #region IFindAndProcess Members

        public bool FindAndProcess(Func<object, bool> find, Func<object, bool> process)
        {
            var r = this.FirstOrDefault(p => find(p));
            if (r != null)
                return !process(r);
            return true;
        }

        #endregion
    }
}
