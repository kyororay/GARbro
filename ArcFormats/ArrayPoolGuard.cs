using System;
using System.Buffers;
using System.Threading;

namespace GameRes.Utility
{
    internal struct ArrayPoolGuard<T> : IDisposable
    {
        private readonly ArrayPool<T> m_pool;
        private T[] m_array;

        public T[] Array
        {
            get
            {
                if (m_array == null)
                    throw new ObjectDisposedException(nameof(ArrayPoolGuard<T>));
                return m_array;
            }
        }

        public ArrayPoolGuard(ArrayPool<T> pool, int minimumLength)
        {
            m_pool = pool;
            m_array = pool.Rent(minimumLength);
            if (m_array == null)
                throw new InvalidOperationException("ArrayPool.Rent returned null");
        }

        public static implicit operator T[](ArrayPoolGuard<T> guard) => guard.Array;

        public void Dispose()
        {
            var oldSavedArray = Interlocked.CompareExchange(ref m_array, null, m_array);
            if (oldSavedArray == null) return;
            m_pool.Return(oldSavedArray);
            m_array = null;
        }

        #region Array Properties

        public T this[int index]
        {
            get => Array[index];
            set => Array[index] = value;
        }

        public int Length => Array.Length;

        #endregion
    }

    internal static class ArrayPoolExtension
    {
        /// <summary>
        /// 配合using语句使用，从pool中借出数组，并在using结束时自动归还
        /// </summary>
        /// <param name="pool">数组池</param>
        /// <param name="minimumLength">数组最小的长度</param>
        /// <typeparam name="T">元素类型</typeparam>
        /// <returns>一个<see cref="ArrayPoolGuard{T}"/>，用于在Dispose方法被调用时归还数组到对象池</returns>
        public static ArrayPoolGuard<T> RentSafe<T>(this ArrayPool<T> pool, int minimumLength)
        {
            return new ArrayPoolGuard<T>(pool, minimumLength);
        }
    }
}