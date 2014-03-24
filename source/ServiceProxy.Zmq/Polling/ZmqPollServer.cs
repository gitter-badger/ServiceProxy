﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZeroMQ;

namespace ServiceProxy.Zmq.Polling
{
    public class ZmqPollServer : IDisposable
    {
        private readonly string brokerBackendAddress;

        private readonly ZeroMQ.ZmqContext zmqContext;

        private long running;
        private volatile Task sendReceiveTask;

        private readonly BlockingCollection<Tuple<byte[], byte[]>> responsesQueue;

        private readonly IServiceFactory serviceFactory;

        public ZmqPollServer(ZeroMQ.ZmqContext zmqContext,
                         string brokerBackendAddress,
                         IServiceFactory serviceFactory)
        {
            this.zmqContext = zmqContext;

            this.brokerBackendAddress = brokerBackendAddress;

            this.responsesQueue = new BlockingCollection<Tuple<byte[], byte[]>>(new ConcurrentQueue<Tuple<byte[], byte[]>>(), int.MaxValue);

            this.serviceFactory = serviceFactory;
        }

        public void Listen()
        {
            this.EnsureIsRunning();
        }

        private void EnsureIsRunning()
        {
            if (Interlocked.CompareExchange(ref this.running, 1, 0) == 0)
            {
                this.sendReceiveTask = Task.Factory.StartNew(this.SendReceive, TaskCreationOptions.LongRunning);
            }
        }

        private void EnsureIsNotRunning()
        {
            if (Interlocked.CompareExchange(ref this.running, 0, 1) == 1)
            {
                this.sendReceiveTask.Wait();
                this.sendReceiveTask = null;
            }
        }

        private void SendReceive()
        {
            using (var socket = this.zmqContext.CreateNonBlockingSocket(ZeroMQ.SocketType.DEALER, TimeSpan.FromMilliseconds(1)))
            {
                //Connect to inbound address
                socket.Connect(this.brokerBackendAddress);
                
                byte[] buffer = new byte[1024];

                var canSend = false;
                var canReceive = false;

                socket.ReceiveReady += (s, e) =>
                {
                    if (canReceive)
                    {
                        byte[] callerId;
                        byte[] requestBytes;

                        int readBytes;
                        callerId = e.Socket.Receive(buffer, out readBytes);
                        if (readBytes > 0)
                        {
                            callerId = callerId.Slice(readBytes);
                            requestBytes = e.Socket.Receive(buffer, out readBytes);
                            this.OnRequest(callerId, requestBytes.Slice(readBytes));
                        }
                    }
                };

                socket.SendReady += (s, e) =>
                {
                    if (canSend)
                    {
                        Tuple<byte[], byte[]> response;
                        while (this.responsesQueue.TryTake(out response))
                        {
                            e.Socket.SendMore(response.Item1); //callerId
                            e.Socket.Send(response.Item2); //response
                        }
                    }
                };

                var poller = new ZeroMQ.Poller(new ZeroMQ.ZmqSocket[] { socket });
                var pollTimeout = TimeSpan.FromMilliseconds(10);

                while (Interlocked.Read(ref this.running) == 1)
                {
                    canSend = responsesQueue.Count > 0;
                    canReceive = true;

                    poller.Poll(pollTimeout);    
                }
            }
        }

        private void OnRequest(byte[] callerId, byte[] requestBytes)
        {
            Task.Run(() =>
            {
                var zmqRequest = ZmqRequest.FromBinary(requestBytes);

                var service = this.serviceFactory.CreateService(zmqRequest.Request.Service);

                service.Process(zmqRequest.Request)
                       .ContinueWith(t =>
                       {
                           var response = t.Result;

                           var zmqResponse = new ZmqResponse(zmqRequest.Id, response);
                           var zmqResponseBytes = zmqResponse.ToBinary();

                           this.responsesQueue.TryAdd(new Tuple<byte[], byte[]>(callerId, zmqResponseBytes));
                       });
            });
        }

        public void Dispose()
        {
            this.EnsureIsNotRunning();
        }
    }
}
