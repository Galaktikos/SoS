using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SoS;

public abstract partial class Server : IDisposable
{
	internal enum Method { AcceptConnect, KeepAlive, Data, Reconnect, Disconnect }

	private protected readonly CancellationTokenSource CloseTokenSource = new();
	private protected readonly Socket Socket;

	internal Server(Socket socket)
	{
		Socket = socket;
	}

	private void SendRawTo(EndPoint endPoint, ReadOnlySpan<byte> data)
	{
#if NET7_0
		Socket.SendTo(data, SocketFlags.None, endPoint);
#else
		Socket.SendTo(data.ToArray(), SocketFlags.None, endPoint);
#endif
	}

	internal void SendTo(EndPoint endPoint, Method method) =>
		SendRawTo(endPoint, new byte[] { (byte)method });

	internal void SendTo(EndPoint endPoint, Method method, ReadOnlySpan<byte> data)
	{
		Span<byte> methodData = new byte[data.Length + 1];
		methodData[0] = (byte)method;
		data.CopyTo(methodData.Slice(1));
		SendRawTo(endPoint, methodData);
	}

	internal abstract void RemoveConnection(IPPort source);

	internal void Close()
	{
		CloseTokenSource.Cancel();
	}

	private bool Disposed = false;
	public void Dispose()
	{
		if (Disposed)
			return;

		CloseTokenSource.Dispose();

		Disposed = true;
		GC.SuppressFinalize(this);
	}

	public record IPPort(IPAddress Address, int Port);
}

public class Server<T> : Server, IDisposable where T : Server.Connection, new()
{
	public Dictionary<IPPort, T> Connections = new();

	private readonly EndPoint ReceiveEndPoint;
	public Server(Socket socket, EndPoint receiveEndPoint) : base(socket)
	{
		ReceiveEndPoint = receiveEndPoint;

#if NET7_0
		ReceiveBuffer = GC.AllocateArray<byte>(length: socket.ReceiveBufferSize, pinned: true).AsMemory();
#else
		ReceiveBuffer = new(new byte[socket.ReceiveBufferSize]);
#endif

		Task.Run(Receive);
	}

	/// <summary>
	/// Sends the specified <paramref name="data"/> to all connections
	/// </summary>
	/// <param name="data"></param>
	/// <exception cref="ObjectDisposedException"></exception>
	public void SendAll(ReadOnlySpan<byte> data)
	{
		if (Disposed)
			throw new ObjectDisposedException(GetType().FullName);

		lock (Connections)
			foreach (Connection connection in Connections.Values)
				connection.Send(data);
	}

	/// <summary>
	/// Closes the server and all connections
	/// </summary>
	/// <exception cref="ObjectDisposedException"></exception>
	public new void Close()
	{
		if (Disposed)
			throw new ObjectDisposedException(GetType().FullName);

		CloseTokenSource.Cancel();

		lock (Connections)
			foreach (Connection connection in Connections.Values)
				connection.Close();

		base.Close();
	}

	private bool Disposed = false;
	public new void Dispose()
	{
		if (Disposed)
			return;

		lock (Connections)
			foreach (Connection connection in Connections.Values)
				connection.Dispose();

		base.Dispose();

		Disposed = true;
		GC.SuppressFinalize(this);
	}

#if NET7_0
	private readonly Memory<byte> ReceiveBuffer;
#else
	private readonly ArraySegment<byte> ReceiveBuffer;
#endif
	private async void Receive()
	{
		try
		{
			while (!CloseTokenSource.Token.IsCancellationRequested)
			{
#if NET7_0
				SocketReceiveFromResult receiveResult = await Socket.ReceiveFromAsync(ReceiveBuffer, SocketFlags.None, ReceiveEndPoint, CloseTokenSource.Token);
#else
				SocketReceiveFromResult receiveResult = await Socket.ReceiveFromAsync(ReceiveBuffer, SocketFlags.None, ReceiveEndPoint);
#endif

#if NET7_0
				Memory<byte> data = ReceiveBuffer[..receiveResult.ReceivedBytes];
#else
				Memory<byte> data = new Memory<byte>(ReceiveBuffer.Array).Slice(0, receiveResult.ReceivedBytes);
#endif

				if (data.Length <= 0)
					return;

				ReadOnlyMemory<byte> methodData = data.Slice(1);
				IPEndPoint ipEndPoint = (IPEndPoint)receiveResult.RemoteEndPoint;
				IPPort sender = new(ipEndPoint.Address, ipEndPoint.Port);

				lock (CloseTokenSource)
				{
					if (CloseTokenSource.IsCancellationRequested)
						return;

					switch ((Client.Method)data.Span[0])
					{
						case Client.Method.Data:
							{
								if (!Connections.TryGetValue(sender, out T? connection))
								{
									SendTo(receiveResult.RemoteEndPoint, Method.Reconnect);
									break;
								}

								connection.OnReceive(methodData.Span);
							}
							break;

						case Client.Method.KeepAlive:
							{
								if (Connections.TryGetValue(sender, out T? connection))
								{
									connection.KeepaliveTimeout();
									SendTo(receiveResult.RemoteEndPoint, Method.KeepAlive);
								}
								else
									SendTo(receiveResult.RemoteEndPoint, Method.Reconnect);
							}
							break;

						case Client.Method.Connect:
							Connections.Add(sender, new()
							{
								Server = this,
								EndPoint = receiveResult.RemoteEndPoint
							});

							SendTo(receiveResult.RemoteEndPoint, Method.AcceptConnect);
							break;

						case Client.Method.Disconnect:
							{
								if (Connections.TryGetValue(sender, out T? connection))
								{
									connection.OnDisconnect();
									connection.Close();
								}
							}
							break;
					}
				}
			}
		}
		catch (OperationCanceledException) { }
	}

	internal override void RemoveConnection(IPPort source) =>
		Connections.Remove(source);
}