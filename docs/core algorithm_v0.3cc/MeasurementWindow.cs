using System;
using System.Collections.Generic;

namespace CrimpHeightControl
{
    /// <summary>
    /// 固定长度滚动窗口（FIFO）。提供均值、截尾均值、样本标准差(n-1)、最小二乘斜率。
    /// 入队顺序即时间顺序（最旧→最新），故斜率为正表示随时间上升。对应 §G 固定窗口、§E 斜率。
    /// 非线程安全：单压头控制器在单线程内顺序调用。
    /// </summary>
    public sealed class MeasurementWindow
    {
        private readonly Queue<double> _buffer;
        private readonly int _capacity;
        private readonly double _trimFraction;

        public MeasurementWindow(int capacity, double trimFraction)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (trimFraction < 0.0 || trimFraction >= 0.5) throw new ArgumentOutOfRangeException(nameof(trimFraction));
            _capacity = capacity;
            _trimFraction = trimFraction;
            _buffer = new Queue<double>(capacity);
        }

        /// <summary>当前样本数。</summary>
        public int Count { get { return _buffer.Count; } }

        /// <summary>窗口容量 n。</summary>
        public int Capacity { get { return _capacity; } }

        /// <summary>窗口是否已满（达到容量）。</summary>
        public bool IsFull { get { return _buffer.Count >= _capacity; } }

        /// <summary>追加一个样本；满则先丢弃最旧（FIFO）。</summary>
        public void Add(double value)
        {
            if (_buffer.Count >= _capacity)
                _buffer.Dequeue();
            _buffer.Enqueue(value);
        }

        /// <summary>清空窗口（epoch HardCut 重置用）。</summary>
        public void Clear()
        {
            _buffer.Clear();
        }

        /// <summary>算术均值；空窗返回 0。</summary>
        public double Mean()
        {
            int n = _buffer.Count;
            if (n == 0) return 0.0;
            double sum = 0.0;
            foreach (double v in _buffer) sum += v;
            return sum / n;
        }

        /// <summary>
        /// 截尾均值：两端各截去 floor(TrimFraction·n) 个样本后求均值。
        /// 若截尾后无剩余（n 太小），回落为普通均值并把 effectiveCount 记为当前 Count。
        /// effectiveCount=0 仅当窗口为空。
        /// </summary>
        public double TrimmedMean(out int effectiveCount)
        {
            int n = _buffer.Count;
            if (n == 0) { effectiveCount = 0; return 0.0; }

            int k = (int)Math.Floor(_trimFraction * n);
            int remaining = n - 2 * k;
            if (remaining <= 0)
            {
                // 样本太少不足以截尾，回落普通均值
                effectiveCount = n;
                return Mean();
            }

            double[] sorted = _buffer.ToArray();
            Array.Sort(sorted);

            double sum = 0.0;
            for (int i = k; i < n - k; i++) sum += sorted[i];
            effectiveCount = remaining;
            return sum / remaining;
        }

        /// <summary>样本标准差（除以 n-1）；样本数 &lt; 2 返回 0。</summary>
        public double StdDev()
        {
            int n = _buffer.Count;
            if (n < 2) return 0.0;

            double mean = Mean();
            double ss = 0.0;
            foreach (double v in _buffer)
            {
                double d = v - mean;
                ss += d * d;
            }
            return Math.Sqrt(ss / (n - 1));
        }

        /// <summary>
        /// 最小二乘斜率（每拍变化量），自变量取索引 0..n-1（最旧→最新）。样本数 &lt; 2 返回 0。
        /// 即 DriftScore（§E）。乘 (n-1) 得窗口跨度内的趋势幅度。
        /// </summary>
        public double Slope()
        {
            int n = _buffer.Count;
            if (n < 2) return 0.0;

            // x 均值 = (n-1)/2
            double xMean = (n - 1) / 2.0;
            double yMean = Mean();

            double sxy = 0.0;
            double sxx = 0.0;
            int x = 0;
            foreach (double y in _buffer)
            {
                double dx = x - xMean;
                sxy += dx * (y - yMean);
                sxx += dx * dx;
                x++;
            }
            if (sxx == 0.0) return 0.0;
            return sxy / sxx;
        }
    }
}
