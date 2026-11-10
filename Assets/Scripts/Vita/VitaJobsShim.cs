// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Unity 2017.4 (the last Unity with PS Vita support) has no Jobs, Collections, Mathematics, Burst or
// Profiling packages. This shim provides the small subset DFU uses so its job code compiles unchanged.
// Jobs run on one background worker thread,
// and JobHandle.Complete() blocks until the job and everything queued before it is done.

#if UNITY_PSP2
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

// Unity 2018 has real NativeArray/Jobs, but its il2cpp remaps Unity.Collections.NativeArray`1 item access to raw pointers, so the
// managed stand-ins can't use Unity's names there. In DaggerfallWorkshop they shadow Unity's types for all DFU code (the enclosing
// namespace wins over `using Unity.Collections`), so 2018 keeps the data in the C# heap like 2017.
#if UNITY_2018_1_OR_NEWER
namespace DaggerfallWorkshop
#else
namespace Unity.Collections
#endif
{
    public enum Allocator { Invalid, None, Temp, TempJob, Persistent }
    public enum NativeArrayOptions { UninitializedMemory, ClearMemory }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter)] public sealed class ReadOnlyAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter)] public sealed class WriteOnlyAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class DeallocateOnJobCompletionAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class NativeDisableParallelForRestrictionAttribute : Attribute { }

    /// <summary>Managed-array stand-in for Unity's NativeArray. Dispose only drops the reference; GC frees it.</summary>
    public struct NativeArray<T> : IDisposable where T : struct
    {
        T[] array;
        int length;

        public NativeArray(int length, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            array = new T[length];
            this.length = length;
        }

        public NativeArray(T[] source, Allocator allocator)
        {
            array = (T[])source.Clone();
            length = array.Length;
        }

        public NativeArray(NativeArray<T> source, Allocator allocator)
        {
            array = source.ToArray();
            length = array.Length;
        }

        /// <summary>View over the first `length` items of an existing array (no copy).</summary>
        internal NativeArray(T[] storage, int length)
        {
            array = storage;
            this.length = length;
        }

        /// <summary>This array's storage seen at a new length, reused when big enough (else +25% headroom); contents are not cleared.</summary>
        internal NativeArray<T> VitaReuse(int newLength)
        {
            if (array != null && array.Length >= newLength)
                return new NativeArray<T>(array, newLength);
            return new NativeArray<T>(new T[newLength + newLength / 4], newLength);
        }

        public T this[int index]
        {
            get { return array[index]; }
            set { array[index] = value; }
        }

        public int Length { get { return length; } }
        public bool IsCreated { get { return array != null; } }

        public void Dispose() { array = null; }
        public JobHandle Dispose(JobHandle inputDeps) { array = null; return inputDeps; }

        public void CopyFrom(T[] source) { Array.Copy(source, array, length); }
        public void CopyFrom(NativeArray<T> source) { Array.Copy(source.array, array, length); }
        public void CopyTo(T[] destination) { Array.Copy(array, destination, length); }
        public T[] ToArray() { var copy = new T[length]; Array.Copy(array, copy, length); return copy; }

        /// <summary>The items as a T[] for Unity 2017 APIs that take arrays (Vita port only). No copy when the array is exactly full.</summary>
        public T[] GetBackingArray() { return array.Length == length ? array : ToArray(); }

        /// <summary>
        /// Same-layout copy into another struct type (e.g. Vector2 -> float2).
        /// </summary>
        public NativeArray<U> Reinterpret<U>() where U : struct
        {
            int bytes = length * Marshal.SizeOf(typeof(T));
            var result = new NativeArray<U>(bytes / Marshal.SizeOf(typeof(U)), Allocator.None);
            var buffer = new byte[bytes];
            GCHandle src = GCHandle.Alloc(array, GCHandleType.Pinned);
            GCHandle dst = GCHandle.Alloc(result.array, GCHandleType.Pinned);
            try
            {
                Marshal.Copy(src.AddrOfPinnedObject(), buffer, 0, bytes);
                Marshal.Copy(buffer, 0, dst.AddrOfPinnedObject(), bytes);
            }
            finally
            {
                src.Free();
                dst.Free();
            }
            return result;
        }
    }

    /// <summary>Stand-in for Unity's NativeList. Copies share one storage object, like the real one.</summary>
    public struct NativeList<T> : IDisposable where T : struct
    {
        internal sealed class Storage
        {
            public T[] items;
            public int count;

            public void Add(T value)
            {
                if (count == items.Length)
                    Array.Resize(ref items, Math.Max(4, items.Length * 2));
                items[count++] = value;
            }
        }

        Storage storage;

        public NativeList(int initialCapacity, Allocator allocator)
        {
            storage = new Storage { items = new T[initialCapacity] };
        }

        public T this[int index]
        {
            get { return storage.items[index]; }
            set { storage.items[index] = value; }
        }

        public int Length { get { return storage.count; } }
        public bool IsCreated { get { return storage != null; } }
        public void Add(T value) { storage.Add(value); }
        public void Clear() { storage.count = 0; }
        public void Dispose() { storage = null; }
        public NativeArray<T> AsArray() { return new NativeArray<T>(storage.items, storage.count); }
        public ParallelWriter AsParallelWriter() { return new ParallelWriter(storage); }

        public static implicit operator NativeArray<T>(NativeList<T> list) { return list.AsArray(); }

        public struct ParallelWriter
        {
            readonly Storage storage;
            internal ParallelWriter(Storage storage) { this.storage = storage; }
            public void AddNoResize(T value) { storage.Add(value); }
        }
    }

    /// <summary>
    /// Vita port: Unity 2017/2018 meshes take managed arrays only (the NativeArray mesh API is 2019.3). Copies into a reused array,
    /// so a mesh update makes no garbage while the vertex count stays the same.
    /// </summary>
    public static class VitaMeshArrays
    {
        public static Vector3[] ToVector3s(this NativeArray<Unity.Mathematics.float3> source, ref Vector3[] cache)
        {
            if (cache == null || cache.Length != source.Length)
                cache = new Vector3[source.Length];
            for (int i = 0; i < cache.Length; i++)
                cache[i] = source[i];
            return cache;
        }

        public static Vector4[] ToVector4s(this NativeArray<Unity.Mathematics.float4> source, ref Vector4[] cache)
        {
            if (cache == null || cache.Length != source.Length)
                cache = new Vector4[source.Length];
            for (int i = 0; i < cache.Length; i++)
                cache[i] = source[i];
            return cache;
        }

        // Fill a reused list (Mesh.SetVertices/SetUVs/... take Lists in 2018.2): no new array when the count changes
        public static List<Vector3> ToList(this NativeArray<Unity.Mathematics.float3> source, List<Vector3> list)
        {
            list.Clear();
            for (int i = 0; i < source.Length; i++)
                list.Add(source[i]);
            return list;
        }

        public static List<Vector4> ToList(this NativeArray<Unity.Mathematics.float4> source, List<Vector4> list)
        {
            list.Clear();
            for (int i = 0; i < source.Length; i++)
                list.Add(source[i]);
            return list;
        }

        public static List<Vector2> ToList(this NativeArray<Unity.Mathematics.float2> source, List<Vector2> list)
        {
            list.Clear();
            for (int i = 0; i < source.Length; i++)
                list.Add(source[i]);
            return list;
        }

        public static List<int> ToList(this NativeArray<ushort> source, List<int> list)
        {
            list.Clear();
            for (int i = 0; i < source.Length; i++)
                list.Add(source[i]);
            return list;
        }

        public static Vector2[] ToVector2s(this NativeArray<Unity.Mathematics.float2> source, ref Vector2[] cache)
        {
            if (cache == null || cache.Length != source.Length)
                cache = new Vector2[source.Length];
            for (int i = 0; i < cache.Length; i++)
                cache[i] = source[i];
            return cache;
        }

        /// <summary>Stands in for new NativeArray&lt;Vector2&gt;(source, allocator).Reinterpret&lt;float2&gt;() (no Reinterpret in 2018.2).</summary>
        public static NativeArray<Unity.Mathematics.float2> ToFloat2s(this Vector2[] source, Allocator allocator)
        {
            var result = new NativeArray<Unity.Mathematics.float2>(source.Length, allocator);
            for (int i = 0; i < source.Length; i++)
                result[i] = source[i];
            return result;
        }
    }
}

#if UNITY_2018_1_OR_NEWER
namespace DaggerfallWorkshop
#else
namespace Unity.Jobs
#endif
{
    public interface IJob { void Execute(); }
    public interface IJobParallelFor { void Execute(int index); }

    /// <summary>
    /// Runs scheduled jobs on one background worker thread (FIFO), so terrain generation leaves the main core (CPU0).
    /// One worker + FIFO means a job never starts before the jobs queued ahead of it, so dependencies hold without extra checks.
    /// One worker, and IJobParallelFor loops run sequentially; two workers would help only if the worker becomes the limit.
    /// </summary>
    public static class VitaJobsShim
    {
        /// <summary>Test switch: false runs every job inside Schedule() on the calling thread.</summary>
        public static bool Threaded = false;   // off: on hardware the worker made loads slower

        /// <summary>
        /// While true, scheduled jobs are queued for Pump() on the main thread instead of running (or going to the worker). Set only around
        /// one terrain tile's BeginMapPixelDataUpdate in play, so a new tile costs a few ms per frame instead of one ~200 ms frame.
        /// </summary>
        public static bool Deferred;
        /// <summary>Main-thread time Pump() spends per frame on deferred jobs.</summary>
        public static float PumpBudgetMs = 6;

        internal sealed class Work
        {
            public Action run;          // null for a CombineDependencies marker
            public Action<int> item;    // IJobParallelFor body, run for indices next..count-1 (resumable)
            public int count, next;
            public bool deferred;
            public Work dep0, dep1;
            public volatile bool done;
            public Exception error;
        }

        static readonly object sync = new object();
        static readonly Queue<Work> queue = new Queue<Work>();
        static Thread worker;
        static readonly Queue<Work> deferredQueue = new Queue<Work>();
        static readonly System.Diagnostics.Stopwatch pumpWatch = new System.Diagnostics.Stopwatch();
        static bool logged;

        // Billboard batch jobs stay on the calling thread: its main-thread code shares NativeList storage with them and was
        // written for jobs that finish inside Schedule().
        static class Sync<T>
        {
            public static readonly bool Value = typeof(T).DeclaringType != null && typeof(T).DeclaringType.Name == "DaggerfallBillboardBatch";
        }

        internal static bool UseWorker<T>()
        {
            if (!logged)
            {
                logged = true;
                Debug.Log("VITAJOBS threaded " + Threaded);
            }
            return Threaded && !Sync<T>.Value;
        }

        internal static bool UseQueue<T>()
        {
            return Deferred ? !Sync<T>.Value : UseWorker<T>();
        }

        internal static JobHandle Enqueue(Action run, Work dep0, Work dep1)
        {
            return Enqueue(run, null, 0, dep0, dep1);
        }

        internal static JobHandle Enqueue(Action run, Action<int> item, int count, Work dep0, Work dep1)
        {
            if (run == null && item == null && dep0 == null && dep1 == null)
                return default(JobHandle);
            var work = new Work { run = run, item = item, count = count, dep0 = dep0, dep1 = dep1, deferred = Deferred };
            if (Deferred)
            {
                deferredQueue.Enqueue(work);
                return new JobHandle(work);
            }
            lock (sync)
            {
                if (worker == null)
                {
                    worker = new Thread(WorkerLoop) { IsBackground = true, Name = "VitaJobs" };
                    worker.Start();
                }
                queue.Enqueue(work);
                Monitor.PulseAll(sync);
            }
            return new JobHandle(work);
        }

        static void WorkerLoop()
        {
            while (true)
            {
                Work work;
                lock (sync)
                {
                    while (queue.Count == 0)
                        Monitor.Wait(sync);     // idle in a real blocking wait (no spinning: the Vita scheduler isn't time-sliced)
                    work = queue.Dequeue();
                }

                Step(work, 0);
                lock (sync)
                {
                    work.done = true;
                    Monitor.PulseAll(sync);
                }
            }
        }

        // Runs a job until it ends (returns true) or, with budgetTicks > 0, until pumpWatch passes the budget (false: call again).
        static bool Step(Work work, long budgetTicks)
        {
            // A failed dependency fails this job too (it would read half-made data), so Complete() on the last handle reports it
            Exception error = work.dep0 != null && work.dep0.error != null ? work.dep0.error :
                work.dep1 != null && work.dep1.error != null ? work.dep1.error : null;
            if (error == null)
            {
                try
                {
                    if (work.run != null)
                        work.run();
                    while (work.next < work.count)
                    {
                        work.item(work.next++);
                        if (budgetTicks > 0 && (work.next & 63) == 0 && pumpWatch.ElapsedTicks >= budgetTicks)
                            return false;
                    }
                }
                catch (Exception e) { error = e; }
            }
            work.error = error;
            return true;
        }

        /// <summary>Runs deferred jobs in queue order for about budgetMs on the calling (main) thread, or until target is done.</summary>
        public static void Pump(float budgetMs)
        {
            Pump(budgetMs, null);
        }

        static void Pump(float budgetMs, Work target)
        {
            long budgetTicks = target != null || budgetMs >= 1e6f ? 0 : Math.Max(1, (long)(budgetMs * System.Diagnostics.Stopwatch.Frequency / 1000));
            pumpWatch.Reset();
            pumpWatch.Start();
            while (deferredQueue.Count > 0 && (target == null || !target.done))
            {
                Work work = deferredQueue.Peek();
                // Dependencies queued earlier are done (FIFO); one on the worker thread is waited for
                if (work.dep0 != null && !work.dep0.deferred) Wait(work.dep0);
                if (work.dep1 != null && !work.dep1.deferred) Wait(work.dep1);
                if (!Step(work, budgetTicks))
                    return;
                deferredQueue.Dequeue();
                work.done = true;
                if (budgetTicks > 0 && pumpWatch.ElapsedTicks >= budgetTicks)
                    return;
            }
        }

        /// <summary>
        /// Drops every queued deferred job (marked done with an error, so a Complete() on it throws): for a world clear, when the queue
        /// can only hold the old world's tile (init builds run synchronously). Its coroutine stops on its own (StreamingWorld.vitaWorldGen).
        /// </summary>
        public static void DropDeferred()
        {
            while (deferredQueue.Count > 0)
            {
                Work work = deferredQueue.Dequeue();
                work.error = new OperationCanceledException("VITAJOBS dropped at a world clear");
                work.done = true;
            }
        }

        internal static void Wait(Work work)
        {
            if (work.deferred && !work.done)
                Pump(0, work);
            if (!work.done)
            {
                lock (sync)
                {
                    while (!work.done)
                        Monitor.Wait(sync);
                }
            }
            if (work.error != null)
                throw new InvalidOperationException("VITAJOBS job failed on the worker thread", work.error);
        }
    }

    /// <summary>Handle of a job queued on the VitaJobsShim worker; default (no work) is always complete.</summary>
    public struct JobHandle
    {
        internal readonly VitaJobsShim.Work work;
        internal JobHandle(VitaJobsShim.Work work) { this.work = work; }

        public bool IsCompleted { get { return work == null || work.done; } }
        public void Complete() { if (work != null) VitaJobsShim.Wait(work); }
        public static void ScheduleBatchedJobs() { }
        public static JobHandle CombineDependencies(JobHandle job0, JobHandle job1) { return VitaJobsShim.Enqueue(null, job0.work, job1.work); }
        public static JobHandle CombineDependencies(JobHandle job0, JobHandle job1, JobHandle job2) { return CombineDependencies(CombineDependencies(job0, job1), job2); }
        public static JobHandle CombineDependencies(NativeArray<JobHandle> jobs)
        {
            JobHandle combined = default(JobHandle);
            for (int i = 0; i < jobs.Length; i++)
                combined = CombineDependencies(combined, jobs[i]);
            return combined;
        }
    }

    public static class IJobExtensions
    {
        public static JobHandle Schedule<T>(this T jobData, JobHandle dependsOn = default(JobHandle)) where T : struct, IJob
        {
            if (VitaJobsShim.UseQueue<T>())
                return VitaJobsShim.Enqueue(() => jobData.Execute(), dependsOn.work, null);
            dependsOn.Complete();
            jobData.Execute();
            return default(JobHandle);
        }

        public static void Run<T>(this T jobData) where T : struct, IJob
        {
            jobData.Execute();
        }
    }

    public static class IJobParallelForExtensions
    {
        public static JobHandle Schedule<T>(this T jobData, int arrayLength, int innerloopBatchCount, JobHandle dependsOn = default(JobHandle)) where T : struct, IJobParallelFor
        {
            if (VitaJobsShim.UseQueue<T>())
                return VitaJobsShim.Enqueue(null, i => jobData.Execute(i), arrayLength, dependsOn.work, null);
            dependsOn.Complete();
            for (int i = 0; i < arrayLength; i++)
                jobData.Execute(i);
            return default(JobHandle);
        }

        public static void Run<T>(this T jobData, int arrayLength) where T : struct, IJobParallelFor
        {
            for (int i = 0; i < arrayLength; i++)
                jobData.Execute(i);
        }
    }
}

namespace Unity.Burst
{
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class BurstCompileAttribute : Attribute { }
}

namespace Unity.Profiling
{
    public struct ProfilerMarker
    {
        readonly string name;
        public ProfilerMarker(string name) { this.name = name; }
        public void Begin() { UnityEngine.Profiling.Profiler.BeginSample(name); }
        public void End() { UnityEngine.Profiling.Profiler.EndSample(); }
        public AutoScope Auto() { Begin(); return new AutoScope(); }

        public struct AutoScope : IDisposable
        {
            public void Dispose() { UnityEngine.Profiling.Profiler.EndSample(); }
        }
    }
}

namespace Unity.Mathematics
{
    public struct float2
    {
        public float x, y;
        public float2(float x, float y) { this.x = x; this.y = y; }
        public static float2 operator +(float2 a, float2 b) { return new float2(a.x + b.x, a.y + b.y); }
        public static float2 operator -(float2 a, float2 b) { return new float2(a.x - b.x, a.y - b.y); }
        public static float2 operator *(float2 a, float2 b) { return new float2(a.x * b.x, a.y * b.y); }
        public static float2 operator /(float2 a, float2 b) { return new float2(a.x / b.x, a.y / b.y); }
        public static float2 operator *(float2 a, float s) { return new float2(a.x * s, a.y * s); }
        public static float2 operator *(float s, float2 a) { return new float2(a.x * s, a.y * s); }
        public static float2 operator /(float2 a, float s) { return new float2(a.x / s, a.y / s); }
        public static float2 operator -(float2 a) { return new float2(-a.x, -a.y); }
        public static implicit operator float2(Vector2 v) { return new float2(v.x, v.y); }
        public static implicit operator Vector2(float2 v) { return new Vector2(v.x, v.y); }
        public override string ToString() { return string.Format("float2({0}, {1})", x, y); }
    }

    public struct float3
    {
        public float x, y, z;
        public float3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static float3 operator +(float3 a, float3 b) { return new float3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static float3 operator -(float3 a, float3 b) { return new float3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static float3 operator *(float3 a, float3 b) { return new float3(a.x * b.x, a.y * b.y, a.z * b.z); }
        public static float3 operator /(float3 a, float3 b) { return new float3(a.x / b.x, a.y / b.y, a.z / b.z); }
        public static float3 operator *(float3 a, float s) { return new float3(a.x * s, a.y * s, a.z * s); }
        public static float3 operator *(float s, float3 a) { return new float3(a.x * s, a.y * s, a.z * s); }
        public static float3 operator /(float3 a, float s) { return new float3(a.x / s, a.y / s, a.z / s); }
        public static float3 operator -(float3 a) { return new float3(-a.x, -a.y, -a.z); }
        public static implicit operator float3(Vector3 v) { return new float3(v.x, v.y, v.z); }
        public static implicit operator Vector3(float3 v) { return new Vector3(v.x, v.y, v.z); }
        public override string ToString() { return string.Format("float3({0}, {1}, {2})", x, y, z); }
    }

    public struct float4
    {
        public float x, y, z, w;
        public float4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static implicit operator float4(Vector4 v) { return new float4(v.x, v.y, v.z, v.w); }
        public static implicit operator Vector4(float4 v) { return new Vector4(v.x, v.y, v.z, v.w); }
        public override string ToString() { return string.Format("float4({0}, {1}, {2}, {3})", x, y, z, w); }
    }

    public struct int2
    {
        public int x, y;
        public int2(int x, int y) { this.x = x; this.y = y; }
        public static explicit operator int2(float2 v) { return new int2((int)v.x, (int)v.y); }
        public static implicit operator float2(int2 v) { return new float2(v.x, v.y); }
    }

    /// <summary>
    /// Port of Unity.Mathematics.Random (xorshift32) with the same outputs, so terrain noise matches other platforms.
    /// </summary>
    public struct Random
    {
        [StructLayout(LayoutKind.Explicit)]
        struct UIntFloat
        {
            [FieldOffset(0)] public uint u;
            [FieldOffset(0)] public float f;
        }

        public uint state;

        public Random(uint seed)
        {
            if (seed == 0)
                throw new ArgumentException("Seed must be non-zero");
            state = seed;
            NextState();
        }

        public static Random CreateFromIndex(uint index)
        {
            return new Random(WangHash(index + 62u));
        }

        static uint WangHash(uint n)
        {
            n = (n ^ 61u) ^ (n >> 16);
            n *= 9u;
            n = n ^ (n >> 4);
            n *= 0x27d4eb2du;
            n = n ^ (n >> 15);
            return n;
        }

        uint NextState()
        {
            uint t = state;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return t;
        }

        public float NextFloat()
        {
            return new UIntFloat { u = 0x3f800000u | (NextState() >> 9) }.f - 1.0f;
        }

        public float NextFloat(float min, float max)
        {
            return NextFloat() * (max - min) + min;
        }

        public int NextInt(int min, int max)
        {
            uint range = (uint)(max - min);
            return (int)(NextState() * (ulong)range >> 32) + min;
        }
    }

    public static class math
    {
        public static int min(int a, int b) { return a < b ? a : b; }
        public static float min(float a, float b) { return a < b ? a : b; }
        public static int max(int a, int b) { return a > b ? a : b; }
        public static float max(float a, float b) { return a > b ? a : b; }
    }
}
#endif
