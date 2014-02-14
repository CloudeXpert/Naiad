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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Naiad.DataStructures;
using System.Diagnostics;
using Naiad.Scheduling;

namespace Naiad.Dataflow.Channels
{
    internal class PipelineChannel<S, T> : Cable<S, T>
        where T : Time<T>
    {
        private class Fiber : SendWire<S, T>, RecvWire<S, T>
        {
            private readonly PipelineChannel<S, T> bundle;
            private readonly int index;
            private VertexInput<S, T> receiver;

            public int RecordSizeHint
            {
                get
                {
                    return int.MaxValue;
                }
            }

            public void Drain() 
            { 
                //throw new Exception("Attempting to Drain a pipeline channel"); 
            }

            public Cable<S, T> ChannelBundle
            {
                get
                {
                    return this.bundle;
                }
            }

            public Fiber(PipelineChannel<S, T> bundle, VertexInput<S, T> receiver, int index)
            {
                this.bundle = bundle;
                this.index = index;
                this.receiver = receiver;
            }

            public void Send(Pair<S, T> record)
            {
                this.receiver.RecordReceived(record, new RemotePostbox());
            }

            public void Send(Message<Pair<S, T>> records)
            {
                this.receiver.MessageReceived(records, new RemotePostbox());
            }

            public override string ToString()
            {
                return string.Format("Pipeline({0} => {1})", this.bundle.SourceStage, this.bundle.DestinationStage);
            }

            public bool Recv(ref Message<Pair<S, T>> message)
            {
                ThreadLocalBufferPools<Pair<S, T>>.pool.Value.CheckIn(message.payload);
                message.payload = null;
                message.length = -1;
                return message.length >= 0;
            }

            public void Flush()
            {
                this.receiver.Flush();
            }
        }

        private readonly StageOutput<S, T> sender;
        private readonly StageInput<S, T> receiver;

        private readonly Dictionary<int, Fiber> subChannels;

        private readonly int channelId;
        public int ChannelId { get { return channelId; } }

        public PipelineChannel(StageOutput<S, T> sender, StageInput<S, T> receiver, int channelId)
        {
            this.sender = sender;
            this.receiver = receiver;

            this.channelId = channelId;

            this.subChannels = new Dictionary<int, Fiber>();
            foreach (VertexLocation loc in sender.ForStage.Placement)
                if (loc.ProcessId == sender.ForStage.InternalGraphManager.Controller.Configuration.ProcessID)
                    this.subChannels[loc.VertexId] = new Fiber(this, receiver.GetPin(loc.VertexId), loc.VertexId);
        }

        public SendWire<S, T> GetSendFiber(int i)
        {
            return this.subChannels[i];
        }

        public RecvWire<S, T> GetRecvFiber(int i)
        {
            return this.subChannels[i];
        }
        
        public Dataflow.Stage SourceStage { get { return this.sender.ForStage; } }
        public Dataflow.Stage DestinationStage { get { return this.receiver.ForStage; } }

        public override string ToString()
        {
            return String.Format("Pipeline channel: {0} -> {1}", this.sender.ForStage, this.receiver.ForStage);
        }
    }
}
