﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ServerCore
{
	public abstract class PacketSession : Session
	{
		public static readonly int HeaderSize = 2;
		//override로 정의, sealed 로 여기의 것을 사용하ㅏ도록
		//[size(2)][packetId(2)]...
        public sealed override int OnRecv(ArraySegment<byte> buffer)
		{
			int processLen=0;
			while (true)
			{
				//최소한 헤더사이즈 받을 수 있는지  
				if (buffer.Count < HeaderSize) 
				{
					break;
				}
				ushort dataSize = BitConverter.ToUInt16(buffer.Array, buffer.Offset);
				if(buffer.Count < dataSize) // packet이 다 안왔다.
				{
					break;
				}

				OnRecvPacket(new ArraySegment<byte>(buffer.Array,buffer.Offset,dataSize));

				processLen += dataSize;

				buffer = new ArraySegment<byte>(buffer.Array, buffer.Offset + dataSize, buffer.Count - dataSize);
			}

			return processLen;
		}

		public abstract void OnRecvPacket(ArraySegment<byte> buffer);
    }

    public abstract class Session
	{
		Socket _socket;
		int _disconnected = 0;

		RecvBuffer _recvBuffer = new RecvBuffer(1024);

        List<ArraySegment<byte>> _pendingList = new List<ArraySegment<byte>>();
        SocketAsyncEventArgs _sendArgs = new SocketAsyncEventArgs();
        SocketAsyncEventArgs _recvArgs = new SocketAsyncEventArgs();
        Queue<ArraySegment<byte>> _sendQueue = new Queue<ArraySegment<byte>>();
		

		object _lock = new object();

		public abstract void OnConnected(EndPoint endPoint);
		public abstract int OnRecv(ArraySegment<byte> buffer);
		public abstract void OnSend(int numOfBytes);
		public abstract void OnDisconnected(EndPoint endPoint);


        public void Start(Socket socket)
		{
			_socket = socket;

			_recvArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnRecvCompleted);

            _sendArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnSendCompleted);

            RegisterRecv();
		}

		public void Send(ArraySegment<byte> sendBuffer)
		{
			//_socket.Send(sendBuffer);
			lock (_lock)
			{
                _sendQueue.Enqueue(sendBuffer);
                if (_pendingList.Count() == 0)
                    RegisterSend();
            }
        }

		public void Disconnect()
		{
			if(Interlocked.Exchange(ref _disconnected, 1) == 1)
			{
				return;
			}
			OnDisconnected(_socket.RemoteEndPoint);
			_socket.Shutdown(SocketShutdown.Both);
			_socket.Close();
		}

        #region network communication

		void RegisterSend()
		{
            while (_sendQueue.Count > 0)
			{
                ArraySegment<byte> buff = _sendQueue.Dequeue();
                _pendingList.Add(buff);
			}
			_sendArgs.BufferList = _pendingList;


            bool pending = _socket.SendAsync(_sendArgs);
			if (pending == false)
				OnSendCompleted(null, _sendArgs);
		}

		void OnSendCompleted(object sender,SocketAsyncEventArgs args)
		{
			lock (_lock)
			{
                if (args.BytesTransferred > 0 && args.SocketError == SocketError.Success)
                {
                    try
                    {
						_sendArgs.BufferList = null;
						_pendingList.Clear();
						if (_sendQueue.Count > 0)
							RegisterSend();

						OnSend(_sendArgs.BytesTransferred);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"OnSendCompleted failed : {e}");
                    }
                }
                else
                {
                    Disconnect();
                }
            }
			
		}


        void RegisterRecv()
		{
			_recvBuffer.Clean();
			ArraySegment<byte> segment = _recvBuffer.WriteSegment;
			_recvArgs.SetBuffer(segment.Array, segment.Offset, segment.Count);

			bool pending = _socket.ReceiveAsync(_recvArgs);
			if (pending == false)
				OnRecvCompleted(null, _recvArgs);
		}

		void OnRecvCompleted(object sender,SocketAsyncEventArgs args)
		{
			if (args.BytesTransferred > 0 && args.SocketError == SocketError.Success)
			{
				try
				{
					//writePos 이동
					if (_recvBuffer.OnWrite(args.BytesTransferred) == false)
					{
						Disconnect();
						return;
					}

					//컨텐츠쪽으로 데이터 넘겨주고 얼마나 받았는지 알아야 함.

                    int processLen = OnRecv(_recvBuffer.ReadSegment);
					if (processLen < 0 || processLen>_recvBuffer.DataSize)
					{
						Disconnect();
						return;
					}

					//_readPos 이동
					if (_recvBuffer.OnRead(processLen) == false)
					{
						Disconnect();
						return;
					}

                    RegisterRecv();
				}catch(Exception e)
				{
					Console.WriteLine($"OnRecvCompleted failed : {e}");
				}
            }
			else
			{
				Disconnect();
			}
			
		}

        #endregion
    }
}

