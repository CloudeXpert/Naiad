/*
 * Naiad ver. 0.2
 * Copyright (c) Microsoft Corporation
 * All rights reserved. 
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0 
 *
 * THIS CODE IS PROVIDED ON AN *AS IS* BASIS, WITHOUT WARRANTIES OR
 * CONDITIONS OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT
 * LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR
 * A PARTICULAR PURPOSE, MERCHANTABLITY OR NON-INFRINGEMENT.
 *
 * See the Apache Version 2.0 License for specific language governing
 * permissions and limitations under the License.
 */

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


using System.Collections.Concurrent;
using System.IO;
using Naiad.FaultTolerance;
using Naiad.DataStructures;
using Naiad.Dataflow.Channels;
using Naiad.Scheduling;
using Naiad.Frameworks.DifferentialDataflow.CollectionTrace;

using System.Linq.Expressions;
using System.Diagnostics;
using Naiad.CodeGeneration;
using Naiad;
using Naiad.Dataflow;
using Naiad.Frameworks;

namespace Naiad.Frameworks.DifferentialDataflow.OperatorImplementations
{
    internal class BinaryStatefulOperator<K, V1, V2, S1, S2, T, R> : BinaryBufferingVertex<Weighted<S1>, Weighted<S2>, Weighted<R>, T>
        //BinaryOperator<S1, S2, R, T>
        where K : IEquatable<K>
        where V1 : IEquatable<V1>
        where V2 : IEquatable<V2>
        where S1 : IEquatable<S1>
        where S2 : IEquatable<S2>
        where T : Time<T>
        where R : IEquatable<R>
    {
        public override void MessageReceived1(Message<Pair<Weighted<S1>, T>> message)
        {
            if (this.inputImmutable1)
            {
                for (int i = 0; i < message.length; i++)
                {
                    this.OnInput1(message.payload[i].v1, message.payload[i].v2);
                    this.NotifyAt(message.payload[i].v2);
                }
            }
            else
                base.MessageReceived1(message);
        }

        public override void MessageReceived2(Message<Pair<Weighted<S2>, T>> message)
        {
            if (this.inputImmutable2)
            {
                for (int i = 0; i < message.length; i++)
                {
                    this.OnInput2(message.payload[i].v1, message.payload[i].v2);
                    this.NotifyAt(message.payload[i].v2);
                }
            }
            else
                base.MessageReceived2(message);
        }

        protected override void OnShutdown()
        {
            base.OnShutdown();

            if (inputTrace1 != null)
                inputTrace1.Release();
            inputTrace1 = null;

            if (inputTrace2 != null)
                inputTrace2.Release();
            inputTrace2 = null;

            if (outputTrace != null)
                outputTrace.Release();
            outputTrace = null;

            keyIndices = null;
            keysToProcess = null;
            internTable = null;

        }

        public override void UpdateReachability(NaiadList<Pointstamp> versions)
        {
            base.UpdateReachability(versions);

            if (versions != null && internTable != null)
                internTable.UpdateReachability(versions);
        }

        //public readonly Naiad.Frameworks.RecvFiberBank<Weighted<S1>, T> LeftInput;
        //public readonly Naiad.Frameworks.RecvFiberBank<Weighted<S2>, T> RightInput;
        
        //public override FiberRecvEndpoint<Weighted<S1>, T> LeftInput { get { return new LeftRecvEndpoint(this); } }
        //public override FiberRecvEndpoint<Weighted<S2>, T> RightInput { get { return new RightRecvEndpoint(this); } }

        public Func<S1, K> key1;      // extracts the key from the input record
        public Func<S1, V1> value1;    // reduces input record to relevant value

        public Func<S2, K> key2;      // extracts the key from the input record
        public Func<S2, V2> value2;    // reduces input record to relevant value

        public Expression<Func<S1, K>> keyExpression1;      // extracts the key from the input record
        public Expression<Func<S1, V1>> valueExpression1;    // reduces input record to relevant value

        public Expression<Func<S2, K>> keyExpression2;      // extracts the key from the input record
        public Expression<Func<S2, V2>> valueExpression2;    // reduces input record to relevant value

        readonly bool MaintainOutputTrace;

        protected CollectionTraceWithHeap<R> createOutputTrace()
        {
            return new CollectionTraceWithHeap<R>((x, y) => internTable.LessThan(x, y), x => internTable.UpdateTime(x), this.Stage.Placement.Count);
        }

        protected CollectionTraceCheckpointable<V2> createInputTrace2()
        {
            if (Naiad.CodeGeneration.ExpressionComparer.Instance.Equals(keyExpression2, valueExpression2))
            {
                if (this.inputImmutable2)
                {
                    Logging.Progress("Allocating immutable no heap in {0}", this);
                    return new CollectionTraceImmutableNoHeap<V2>();
                }
                else
                    return new CollectionTraceWithoutHeap<V2>((x, y) => internTable.LessThan(x, y),
                                                                   x => internTable.UpdateTime(x));
            }
            else
            {
                if (this.inputImmutable2)
                {
                    Logging.Progress("Allocating immutable heap in {0}", this);
                    return new CollectionTraceImmutable<V2>();
                }
                else
                    return new CollectionTraceWithHeap<V2>((x, y) => internTable.LessThan(x, y),
                                                                 x => internTable.UpdateTime(x), this.Stage.Placement.Count);
            }
        }

        protected CollectionTraceCheckpointable<V1> createInputTrace1()
        {
            if (Naiad.CodeGeneration.ExpressionComparer.Instance.Equals(keyExpression1, valueExpression1))
            {
                if (this.inputImmutable1)
                    return new CollectionTraceImmutableNoHeap<V1>();
                else
                    return new CollectionTraceWithoutHeap<V1>((x, y) => internTable.LessThan(x, y),
                                                                    x => internTable.UpdateTime(x));
            }
            else
            {
                if (this.inputImmutable1)
                {
                    Logging.Progress("Allocating immutable heap in {0}", this);
                    return new CollectionTraceImmutable<V1>();
                }
                else
                    return new CollectionTraceWithHeap<V1>((x, y) => internTable.LessThan(x, y),
                                                                 x => internTable.UpdateTime(x), this.Stage.Placement.Count);
            }
        }

        public override void OnDone(T workTime)
        {
            if (!this.inputImmutable1)
            {
                foreach (var record in this.Input1.GetRecordsAt(workTime))
                    OnInput1(record, workTime); 
            }

            if (!this.inputImmutable2)
            {
                foreach (var record in this.Input2.GetRecordsAt(workTime))
                    OnInput2(record, workTime);
            }
            
            Compute();
            Flush();

            //if (this.inputImmutable1 && this.inputImmutable2)
            //    this.ShutDown();
            //else
            {
                if (inputTrace1 != null) inputTrace1.Compact();
                if (inputTrace2 != null) inputTrace2.Compact();
            }
        }

        protected Dictionary<K, BinaryKeyIndices> keyIndices;

        protected CollectionTraceCheckpointable<V1> inputTrace1;          // collects all differences that have processed.
        protected CollectionTraceCheckpointable<V2> inputTrace2;          // collects all differences that have processed.
        protected CollectionTraceCheckpointable<R> outputTrace;             // collects outputs

        protected int outputWorkspace = 0;

        protected LatticeInternTable<T> internTable = new LatticeInternTable<T>();

        protected NaiadList<K> keysToProcess = new NaiadList<K>(1);

        public virtual void OnInput1(Weighted<S1> entry, T time)
        {
            var k = key1(entry.record);

            BinaryKeyIndices state;
            if (!keyIndices.TryGetValue(k, out state))
                state = new BinaryKeyIndices();

            if (state.unprocessed1 == 0 && state.unprocessed2 == 0)
                keysToProcess.Add(k);

            inputTrace1.Introduce(ref state.unprocessed1, value1(entry.record), entry.weight, internTable.Intern(time));

            keyIndices[k] = state;
        }

        public virtual void OnInput2(Weighted<S2> entry, T time)
        {
            var k = key2(entry.record);

            BinaryKeyIndices state;
            if (!keyIndices.TryGetValue(k, out state))
                state = new BinaryKeyIndices();

            if (state.unprocessed1 == 0 && state.unprocessed2 == 0)
                keysToProcess.Add(k);

            inputTrace2.Introduce(ref state.unprocessed2, value2(entry.record), entry.weight, internTable.Intern(time));

            keyIndices[k] = state;
        }

        public virtual void Compute()
        {
            for (int i = 0; i < keysToProcess.Count; i++)
                Update(keysToProcess.Array[i]);

            keysToProcess.Clear();
        }

        protected NaiadList<Weighted<V1>> collection1 = new NaiadList<Weighted<V1>>(1);
        protected NaiadList<Weighted<V1>> difference1 = new NaiadList<Weighted<V1>>(1);

        protected NaiadList<Weighted<V2>> collection2 = new NaiadList<Weighted<V2>>(1);
        protected NaiadList<Weighted<V2>> difference2 = new NaiadList<Weighted<V2>>(1);


        // Moves from unprocessed[key] to processed[key], updating output[key] and Send()ing.
        protected virtual void Update(K key)
        {
            var traceIndices = keyIndices[key];

            if (traceIndices.unprocessed1 != 0 || traceIndices.unprocessed2 != 0)
            {
                inputTrace1.EnsureStateIsCurrentWRTAdvancedTimes(ref traceIndices.processed1);
                inputTrace2.EnsureStateIsCurrentWRTAdvancedTimes(ref traceIndices.processed2);

                // iterate through the times that may require updates.
                var interestingTimes = InterestingTimes(traceIndices);

                // incorporate the updates, so we can compare old and new outputs.
                inputTrace1.IntroduceFrom(ref traceIndices.processed1, ref traceIndices.unprocessed1, false);
                inputTrace2.IntroduceFrom(ref traceIndices.processed2, ref traceIndices.unprocessed2, false);

                for (int i = 0; i < interestingTimes.Count; i++)
                    UpdateTime(key, traceIndices, interestingTimes.Array[i]);

                // clean out the state we just processed
                inputTrace1.ZeroState(ref traceIndices.unprocessed1);
                inputTrace2.ZeroState(ref traceIndices.unprocessed2);

                // move the differences we produced from local to persistent storage.
                if (MaintainOutputTrace)
                {
                    outputTrace.IntroduceFrom(ref traceIndices.output, ref outputWorkspace);
                    outputTrace.EnsureStateIsCurrentWRTAdvancedTimes(ref traceIndices.output);
                }
                else
                    outputTrace.ZeroState(ref outputWorkspace);


                keyIndices[key] = traceIndices;
            }
        }

        protected NaiadList<int> timeList = new NaiadList<int>(1);
        protected NaiadList<int> truthList = new NaiadList<int>(1);
        protected NaiadList<int> deltaList = new NaiadList<int>(1);

        protected virtual NaiadList<int> InterestingTimes(BinaryKeyIndices keyIndex)
        {
            deltaList.Clear();
            inputTrace1.EnumerateTimes(keyIndex.unprocessed1, deltaList);
            inputTrace2.EnumerateTimes(keyIndex.unprocessed2, deltaList);

            truthList.Clear();
            inputTrace1.EnumerateTimes(keyIndex.processed1, truthList);
            inputTrace2.EnumerateTimes(keyIndex.processed2, truthList);

            timeList.Clear();
            this.internTable.InterestingTimes(timeList, truthList, deltaList);

            return timeList;
        }

        protected NaiadList<Weighted<R>> outputCollection = new NaiadList<Weighted<R>>(1);

        protected virtual void UpdateTime(K key, BinaryKeyIndices keyIndex, int timeIndex)
        {
            // subtract out prior records before adding new ones
            outputTrace.SubtractStrictlyPriorDifferences(ref outputWorkspace, timeIndex);

            NewOutputMinusOldOutput(key, keyIndex, timeIndex);

            var outputTime = this.internTable.times[timeIndex];

            outputCollection.Clear();
            outputTrace.EnumerateDifferenceAt(outputWorkspace, timeIndex, outputCollection);
            for (int i = 0; i < outputCollection.Count; i++)
                this.Output.Send(outputCollection.Array[i], outputTime);
        }

        protected virtual void NewOutputMinusOldOutput(K key, BinaryKeyIndices keyIndex, int timeIndex)
        {
            Reduce(key, keyIndex, timeIndex);

            // this suggests we want to init updateToOutput with -output
            outputCollection.Clear();
            outputTrace.EnumerateCollectionAt(keyIndex.output, timeIndex, outputCollection);
            for (int i = 0; i < outputCollection.Count; i++)
                outputTrace.Introduce(ref outputWorkspace, outputCollection.Array[i].record, -outputCollection.Array[i].weight, timeIndex);
        }

        // expected to populate resultList to match reduction(collection.source)
        protected virtual void Reduce(K key, BinaryKeyIndices keyIndex, int time) { }

        /* Checkpoint format:
         * bool terminated
         * if !terminated:
         *     LatticeInternTable<T>                            internTable
         *     CollectionTrace<>                                inputTrace1
         *     CollectionTrace<>                                inputTrace2
         *     CollectionTrace<>                                outputTrace
         *     Dictionary<K,KeyIndices>                         keysToProcess
         *     int                                              recordsToProcessCount1
         *     (T,NaiadList<Weighted<S>>)*recordsToProcessCount recordsToProcess1
         *     int                                              recordsToProcessCount2
         *     (T,NaiadList<Weighted<S>>)*recordsToProcessCount recordsToProcess2
         */

        protected static NaiadSerialization<K> keySerializer = null;
        private static NaiadSerialization<T> timeSerializer = null;
        private static NaiadSerialization<Weighted<S1>> weightedS1Serializer = null;
        private static NaiadSerialization<Weighted<S2>> weightedS2Serializer = null;

        public override void Checkpoint(NaiadWriter writer)
        {
            base.Checkpoint(writer);
            writer.Write(this.isShutdown, PrimitiveSerializers.Bool);
            if (!this.isShutdown)
            {
                if (keySerializer == null)
                    keySerializer = AutoSerialization.GetSerializer<K>();
                if (timeSerializer == null)
                    timeSerializer = AutoSerialization.GetSerializer<T>();
                if (weightedS1Serializer == null)
                    weightedS1Serializer = AutoSerialization.GetSerializer<Weighted<S1>>();
                if (weightedS2Serializer == null)
                    weightedS2Serializer = AutoSerialization.GetSerializer<Weighted<S2>>();

                this.internTable.Checkpoint(writer, timeSerializer);
                this.inputTrace1.Checkpoint(writer);
                this.inputTrace2.Checkpoint(writer);
                this.outputTrace.Checkpoint(writer);

                this.keyIndices.Checkpoint(writer, keySerializer, BinaryKeyIndices.Serializer);

                this.Input1.Checkpoint(writer);
                this.Input2.Checkpoint(writer);

                /*
                writer.Write(this.recordsToProcess1.Count, PrimitiveSerializers.Int32);
                foreach (KeyValuePair<T, NaiadList<Weighted<S1>>> kvp in this.recordsToProcess1)
                {
                    writer.Write(kvp.Key, timeSerializer);
                    kvp.Value.Checkpoint(writer, weightedS1Serializer);
                }

                writer.Write(this.recordsToProcess2.Count, PrimitiveSerializers.Int32);
                foreach (KeyValuePair<T, NaiadList<Weighted<S2>>> kvp in this.recordsToProcess2)
                {
                    writer.Write(kvp.Key, timeSerializer);
                    kvp.Value.Checkpoint(writer, weightedS2Serializer);
                }
                 */
            }
        }

        public override void Restore(NaiadReader reader)
        {
            base.Restore(reader);
            this.isShutdown = reader.Read<bool>(PrimitiveSerializers.Bool);

            if (!this.isShutdown)
            {
                if (keySerializer == null)
                    keySerializer = AutoSerialization.GetSerializer<K>();
                if (timeSerializer == null)
                    timeSerializer = AutoSerialization.GetSerializer<T>();
                if (weightedS1Serializer == null)
                    weightedS1Serializer = AutoSerialization.GetSerializer<Weighted<S1>>();
                if (weightedS2Serializer == null)
                    weightedS2Serializer = AutoSerialization.GetSerializer<Weighted<S2>>();

                this.internTable.Restore(reader, timeSerializer);
                this.inputTrace1.Restore(reader);
                this.inputTrace2.Restore(reader);
                this.outputTrace.Restore(reader);

                this.keyIndices.Restore(reader, keySerializer, BinaryKeyIndices.Serializer);

                this.Input1.Restore(reader);
                this.Input2.Restore(reader);

                /*
                int recordsToProcessCount1 = reader.Read<int>(PrimitiveSerializers.Int32);

                foreach (NaiadList<Weighted<S1>> recordList in this.recordsToProcess1.Values)
                    recordList.Free();
                this.recordsToProcess1.Clear();

                for (int i = 0; i < recordsToProcessCount1; ++i)
                {
                    T key = reader.Read<T>(timeSerializer);
                    NaiadList<Weighted<S1>> value = new NaiadList<Weighted<S1>>();
                    value.Restore(reader, weightedS1Serializer);
                    this.recordsToProcess1[key] = value;
                }

                int recordsToProcessCount2 = reader.Read<int>(PrimitiveSerializers.Int32);

                foreach (NaiadList<Weighted<S2>> recordList in this.recordsToProcess2.Values)
                    recordList.Free();
                this.recordsToProcess2.Clear();

                for (int i = 0; i < recordsToProcessCount2; ++i)
                {
                    T key = reader.Read<T>(timeSerializer);
                    NaiadList<Weighted<S2>> value = new NaiadList<Weighted<S2>>();
                    value.Restore(reader, weightedS2Serializer);
                    this.recordsToProcess2[key] = value;
                }
                 */
            }
        }

        protected readonly bool inputImmutable1 = false;
        protected readonly bool inputImmutable2 = false;

        //protected Naiad.Frameworks.RecvFiberBank<Weighted<S1>, T> RecvInput1;
        //protected ShardInput<Weighted<S1>, T> input1;
        //internal override ShardInput<Weighted<S1>, T> Input1 { get { return this.input1; } }


        //protected Naiad.Frameworks.RecvFiberBank<Weighted<S2>, T> RecvInput2;
        //protected ShardInput<Weighted<S2>, T> input2;
        //internal override ShardInput<Weighted<S2>, T> Input2 { get { return this.input2; } }



        public BinaryStatefulOperator(int index, Stage<T> stage, bool input1Immutable, bool input2Immutable, Expression<Func<S1, K>> k1, Expression<Func<S2, K>> k2, Expression<Func<S1, V1>> v1, Expression<Func<S2, V2>> v2, bool maintainOC = true)
            : base(index, stage, null)
        {
            key1 = k1.Compile();
            value1 = v1.Compile();

            key2 = k2.Compile();
            value2 = v2.Compile();

            keyExpression1 = k1;
            keyExpression2 = k2;
            valueExpression1 = v1;
            valueExpression2 = v2;

            MaintainOutputTrace = maintainOC;

            inputImmutable1 = input1Immutable;
            inputImmutable2 = input2Immutable;

            keyIndices = new Dictionary<K, BinaryKeyIndices>();
            inputTrace1 = createInputTrace1();
            inputTrace2 = createInputTrace2();
            outputTrace = createOutputTrace();

            outputWorkspace = 0;
        }
    }

    internal abstract class BinaryStatefulOperator<S, T> : OperatorImplementations.BinaryStatefulOperator<S, S, S, S, S, T, S>
        where S : IEquatable<S>
        where T : Time<T>
    {
        protected abstract Int64 WeightFunction(Int64 weight1, Int64 weight2);

        protected override void NewOutputMinusOldOutput(S key, BinaryKeyIndices keyIndices, int timeIndex)
        {
            collection1.Clear();
            inputTrace1.EnumerateCollectionAt(keyIndices.processed1, timeIndex, collection1);

            collection2.Clear();
            inputTrace2.EnumerateCollectionAt(keyIndices.processed2, timeIndex, collection2);

            var newSum1 = 0L;
            for (int i = 0; i < collection1.Count; i++)
                newSum1 += collection1.Array[i].weight;

            var newSum2 = 0L;
            for (int i = 0; i < collection2.Count; i++)
                newSum2 += collection2.Array[i].weight;

            difference1.Clear();
            inputTrace1.EnumerateCollectionAt(keyIndices.unprocessed1, timeIndex, difference1);

            difference2.Clear();
            inputTrace2.EnumerateCollectionAt(keyIndices.unprocessed2, timeIndex, difference2);

            var oldSum1 = newSum1;
            for (int i = 0; i < difference1.Count; i++)
                oldSum1 -= difference1.Array[i].weight;

            var oldSum2 = newSum2;
            for (int i = 0; i < difference2.Count; i++)
                oldSum2 -= difference2.Array[i].weight;

            var oldOut = WeightFunction(oldSum1, oldSum2);
            var newOut = WeightFunction(newSum1, newSum2);

            if (oldOut != newOut)
                outputTrace.Introduce(ref outputWorkspace, key, newOut - oldOut, timeIndex);
        }

        public BinaryStatefulOperator(int index, Stage<T> stage, bool input1Immutable, bool input2Immutable)
            : base(index, stage, input1Immutable, input2Immutable, x => x, x => x, x => x, x => x, false)
        {
        }
    }
}
