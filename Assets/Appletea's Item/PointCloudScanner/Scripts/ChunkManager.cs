using System;
using System.Collections.Generic;
using System.Drawing;
using UnityEngine;

namespace Appletea.Dev.PointCloud
{
    public class ChunkManager
    {
        private Dictionary<(int, int, int), LimitedQueue<Vector3>> chunks;
        private int chunkSize;
        private int maxPointsPerChunk;

        public ChunkManager(int chunkSize, int maxPointsPerChunk)
        {
            this.chunkSize = chunkSize;
            this.maxPointsPerChunk = maxPointsPerChunk;
            chunks = new Dictionary<(int, int, int), LimitedQueue<Vector3>>();
        }

        // --- 在这里添加 Clear 方法 ---
        /// <summary>
        /// 清空管理器中存储的所有块和点。
        /// </summary>
        public void Clear()
        {
            if (chunks != null)
            {
                chunks.Clear(); // 直接调用字典的 Clear() 方法
                Debug.Log("ChunkManager: 所有数据块已清空。");
            }
            else
            {
                // 理论上构造函数会初始化，但以防万一
                Debug.LogWarning("ChunkManager: 'chunks' 字典为空，无法清空。可能未正确初始化？");
                // 如果需要，可以重新初始化
                // chunks = new Dictionary<(int, int, int), LimitedQueue<Vector3>>();
            }
        }
        // --- Clear 方法结束 ---


        // --- 添加这个获取近似边界的方法 ---
        /// <summary>
        /// 计算一个能够包围所有包含点的块的近似边界框。
        /// 效率较高，因为它只检查块索引，不遍历所有点。
        /// </summary>
        /// <returns>包围所有占用块的 Bounds 对象。如果没有点，返回零大小 Bounds。</returns>
        public Bounds GetApproximateBounds()
        {
            // 检查字典是否初始化且有内容
            if (chunks == null || chunks.Count == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero); // 没有块，就没有边界
            }

            // 初始化最小/最大块索引
            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;

            // 遍历所有块的索引 (字典的键)
            foreach (var key in chunks.Keys)
            {
                minX = Mathf.Min(minX, key.Item1);
                minY = Mathf.Min(minY, key.Item2);
                minZ = Mathf.Min(minZ, key.Item3);

                maxX = Mathf.Max(maxX, key.Item1);
                maxY = Mathf.Max(maxY, key.Item2);
                maxZ = Mathf.Max(maxZ, key.Item3);
            }

            // 根据最小/最大块索引和块大小计算世界坐标边界
            // 最小世界坐标 = 最小索引对应的块的最小角点
            Vector3 minWorldBound = new Vector3(minX * chunkSize, minY * chunkSize, minZ * chunkSize);
            // 最大世界坐标 = 最大索引对应的块的最大角点
            // 注意：块的范围是从 index * size 到 (index + 1) * size
            Vector3 maxWorldBound = new Vector3((maxX + 1) * chunkSize, (maxY + 1) * chunkSize, (maxZ + 1) * chunkSize);

            // 计算 Bounds 的中心和大小
            Vector3 center = (minWorldBound + maxWorldBound) / 2.0f;
            Vector3 size = maxWorldBound - minWorldBound;

            return new Bounds(center, size);
        }
        public Bounds GetPreciseBounds()
        {
            // 检查字典是否初始化且有内容
            if (chunks == null || chunks.Count == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero); // 没有块，就没有边界
            }

            Vector3 minPoint = Vector3.positiveInfinity; // 初始化为最大值
            Vector3 maxPoint = Vector3.negativeInfinity; // 初始化为最小值
            bool hasPoints = false;

            // 遍历所有块中的所有点
            foreach (var chunkQueue in chunks.Values)
            {
                // 使用 C# 8.0+ 的 foreach (var point in chunkQueue)
                // 如果环境不支持，可以用 GetEnumerator()
                foreach (Vector3 point in chunkQueue)
                {
                    minPoint = Vector3.Min(minPoint, point);
                    maxPoint = Vector3.Max(maxPoint, point);
                    hasPoints = true; // 标记至少找到了一个点
                }
                // 如果需要兼容旧版本 .NET/Unity:
                // IEnumerator<Vector3> enumerator = chunkQueue.GetEnumerator();
                // while (enumerator.MoveNext())
                // {
                //     Vector3 point = enumerator.Current;
                //     minPoint = Vector3.Min(minPoint, point);
                //     maxPoint = Vector3.Max(maxPoint, point);
                //     hasPoints = true;
                // }
            }

            // 如果遍历后没有找到任何点（理论上不应该发生，除非所有块都意外为空）
            if (!hasPoints)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }

            // 根据最小/最大点计算 Bounds 的中心和大小
            Vector3 center = (minPoint + maxPoint) / 2.0f;
            Vector3 size = maxPoint - minPoint;

            // 处理size为零的情况（例如只有一个点）
            if (size.x < Mathf.Epsilon) size.x = Mathf.Epsilon;
            if (size.y < Mathf.Epsilon) size.y = Mathf.Epsilon;
            if (size.z < Mathf.Epsilon) size.z = Mathf.Epsilon;


            return new Bounds(center, size);
        }

        /// <summary>
        /// 通过仅检查边界块中的点来计算精确的边界框。
        /// 比遍历所有点更快，但比纯基于块索引的方法慢。
        /// </summary>
        /// <returns>包围所有点的精确 Bounds 对象。如果没有点，返回零大小 Bounds。</returns>
        public Bounds GetPreciseBoundsByBoundaryChunks()
        {
            // 检查字典是否初始化且有内容
            if (chunks == null || chunks.Count == 0)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }

            // 1. 找到最小/最大块索引 (与 GetApproximateBounds 类似)
            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
            bool hasChunks = false;

            foreach (var key in chunks.Keys)
            {
                minX = Mathf.Min(minX, key.Item1);
                minY = Mathf.Min(minY, key.Item2);
                minZ = Mathf.Min(minZ, key.Item3);
                maxX = Mathf.Max(maxX, key.Item1);
                maxY = Mathf.Max(maxY, key.Item2);
                maxZ = Mathf.Max(maxZ, key.Item3);
                hasChunks = true;
            }

            // 如果没有块（理论上不会发生，除非chunks被外部清空）
            if (!hasChunks)
            {
                return new Bounds(Vector3.zero, Vector3.zero);
            }


            // 2. 遍历位于边界上的块中的点
            Vector3 minPoint = Vector3.positiveInfinity;
            Vector3 maxPoint = Vector3.negativeInfinity;
            bool hasPoints = false;

            foreach (var kvp in chunks)
            {
                var key = kvp.Key;
                // 检查当前块是否至少有一个索引处于边界上
                if (key.Item1 == minX || key.Item1 == maxX ||
                    key.Item2 == minY || key.Item2 == maxY ||
                    key.Item3 == minZ || key.Item3 == maxZ)
                {
                    // 遍历这个边界块中的所有点
                    foreach (Vector3 point in kvp.Value)
                    {
                        minPoint = Vector3.Min(minPoint, point);
                        maxPoint = Vector3.Max(maxPoint, point);
                        hasPoints = true;
                    }
                    // IEnumerator<Vector3> enumerator = kvp.Value.GetEnumerator();
                    // while (enumerator.MoveNext())
                    // {
                    //     Vector3 point = enumerator.Current;
                    //     minPoint = Vector3.Min(minPoint, point);
                    //     maxPoint = Vector3.Max(maxPoint, point);
                    //     hasPoints = true;
                    // }
                }
            }

            // 如果边界块中没有找到点（例如，所有块都为空，或者代码逻辑错误）
            if (!hasPoints)
            {
                // 可以选择返回近似边界，或者返回零边界
                // return GetApproximateBounds(); // 返回近似值可能更合理
                return new Bounds(Vector3.zero, Vector3.zero); // 或者严格返回零
            }

            // 3. 根据找到的精确最小/最大点计算 Bounds
            Vector3 center = (minPoint + maxPoint) / 2.0f;
            Vector3 size = maxPoint - minPoint;

            // 处理size为零的情况
            if (size.x < Mathf.Epsilon) size.x = Mathf.Epsilon;
            if (size.y < Mathf.Epsilon) size.y = Mathf.Epsilon;
            if (size.z < Mathf.Epsilon) size.z = Mathf.Epsilon;

            return new Bounds(center, size);
        }

        public void AddPoint(Vector3 point)
        {
            var chunkIndex = (
                Mathf.FloorToInt(point.x / chunkSize),
                Mathf.FloorToInt(point.y / chunkSize),
                Mathf.FloorToInt(point.z / chunkSize));

            if (!chunks.TryGetValue(chunkIndex, out var chunk))
            {
                chunk = new LimitedQueue<Vector3> { Limit = maxPointsPerChunk };
                chunks[chunkIndex] = chunk;
            }

            chunk.Enqueue(point);
        }

        private Vector3 ChunkIndexToVector3((int, int, int) chunkIndex, float chunkSize)
        {
            // Chunk‚Ì’†S“_‚ðŒvŽZ‚·‚é
            float x = chunkIndex.Item1 * chunkSize + chunkSize / 2.0f;
            float y = chunkIndex.Item2 * chunkSize + chunkSize / 2.0f;
            float z = chunkIndex.Item3 * chunkSize + chunkSize / 2.0f;

            return new Vector3(x, y, z);
        }

        public List<Vector3> GetPointsInRadius(Vector3 center, float radius, int maxChunkCount)
        {
            List<(float distance, LimitedQueue<Vector3> points)> chunksWithDistance = new List<(float, LimitedQueue<Vector3>)>();

            foreach (var kvp in chunks)
            {
                Vector3 chunkPos = ChunkIndexToVector3(kvp.Key, chunkSize);
                float distance = Vector3.Distance(center, chunkPos);
                if (distance < radius)
                {
                    chunksWithDistance.Add((distance, kvp.Value));
                }
            }

            chunksWithDistance.Sort((a, b) => a.distance.CompareTo(b.distance));

            if (chunksWithDistance.Count > maxChunkCount)
            {
                chunksWithDistance.RemoveRange(maxChunkCount, chunksWithDistance.Count - maxChunkCount);
            }

            List<Vector3> result = new List<Vector3>();
            foreach (var (_, points) in chunksWithDistance)
            {
                result.AddRange(points);
            }

            return result;
        }


        public List<Vector3> GetAllPoints()
        {
            List<Vector3> allPoints = new List<Vector3>();

            foreach (var chunk in chunks.Values)
            {
                allPoints.AddRange(chunk);
            }

            return allPoints;
        }

        
    }

    public static class ListExtensions
    {
        private static System.Random rng = new System.Random();

        // Fisher-Yates Shuffle
        public static void Shuffle<T>(this List<T> list)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1); // 0 <= k <= n
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }
    }

    public class LimitedQueue<T> : Queue<T>
    {
        public int Limit { get; set; }
        public new void Enqueue(T item)
        {
            while (Count >= Limit)
            {
                Dequeue();
            }
            base.Enqueue(item);
        }
    }
}