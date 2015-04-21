﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Framework.TestHost
{
    public class ReportingChannel : IDisposable
    {
        public static async Task<ReportingChannel> ListenOn(int port)
        {
            // This fixes the mono incompatibility but ties it to ipv4 connections
            using (var listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                listenSocket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                listenSocket.Listen(10);

                var socket = await AcceptAsync(listenSocket);

                return new ReportingChannel(socket);
            }
        }

        private readonly BinaryWriter _writer;
        private readonly BinaryReader _reader;
        private readonly ManualResetEventSlim _ackWaitHandle;

        private ReportingChannel(Socket socket)
        {
            Socket = socket;

            var stream = new NetworkStream(Socket);
            _writer = new BinaryWriter(stream);
            _reader = new BinaryReader(stream);
            _ackWaitHandle = new ManualResetEventSlim();

            ReadQueue = new BlockingCollection<Message>(boundedCapacity: 1);

            // Read incoming messages on the background thread
            new Thread(ReadMessages) { IsBackground = true }.Start();
        }

        public BlockingCollection<Message> ReadQueue { get; }

        public Socket Socket { get; private set; }

        public void Send(Message message)
        {
            lock (_writer)
            {
                try
                {
                    Trace.TraceInformation("[ReportingChannel]: Send({0})", message);
                    _writer.Write(JsonConvert.SerializeObject(message));
                }
                catch (Exception ex)
                {
                    Trace.TraceInformation("[ReportingChannel]: Error sending {0}", ex);
                    throw;
                }
            }
        }

        public void SendError(string error)
        {
            Send(new Message()
            {
                MessageType = "Error",
                Payload = JToken.FromObject(new ErrorMessage()
                {
                    Message = error,
                }),
            });
        }

        public void SendError(Exception ex)
        {
            SendError(ex.Message);
        }

        private void ReadMessages()
        {
            while (true)
            {
                try
                {
                    var message = JsonConvert.DeserializeObject<Message>(_reader.ReadString());
                    ReadQueue.Add(message);

                    if (string.Equals(message.MessageType, "TestHost.Acknowledge"))
                    {
                        _ackWaitHandle.Set();
                        ReadQueue.CompleteAdding();
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceInformation("[ReportingChannel]: Waiting for message failed {0}", ex);
                    throw;
                }
            }
        }

        public void Dispose()
        {
            // Wait for a graceful disconnect - drain the queue until we get an 'ACK'
            Message message;
            while (ReadQueue.TryTake(out message, millisecondsTimeout: 1))
            {
            }

            if (_ackWaitHandle.Wait(TimeSpan.FromSeconds(10)))
            {
                Trace.TraceInformation("[ReportingChannel]: Received for ack from test host");
            }
            else
            {
                Trace.TraceInformation("[ReportingChannel]: Timed out waiting for ack from test host");
            }

            Socket.Dispose();
        }

        private static Task<Socket> AcceptAsync(Socket socket)
        {
            return Task.Factory.FromAsync((cb, state) => socket.BeginAccept(cb, state), ar => socket.EndAccept(ar), null);
        }
    }
}