using System.Net.Sockets;
using System.Net;
using System;
using System.Threading.Tasks;
using System.Threading;

namespace SoS;

public abstract class Client : IDisposable
{
	internal enum Method { Connect, KeepAlive, Data, Disconnect }

	public bool Connected { get; private set; } = false;

	private readonly CancellationTokenSource CloseTokenSource = new();

	private readonly Socket Socket;
	private readonly EndPoint ServerEndPoint;
	public Client(Socket socket, EndPoint serverEndPoint)
	{
		Socket = socket;
		ServerEndPoint = serverEndPoint;

#if NET7_0
		ReceiveBuffer = GC.AllocateArray<byte>(length: socket.ReceiveBufferSize, pinned: true).AsMemory();
#else
		ReceiveBuffer = new(new byte[socket.ReceiveBufferSize]);
#endif

		Task.Run(Receive);
		Task.Run(Update);
	}

	/// <summary>
	/// Sends the specified <paramref name="data"/> to the server
	/// </summary>
	/// <param name="data"></param>
	/// <exception cref="ObjectDisposedException"></exception>
	/// <exception cref="NullReferenceException"></exception>
	public void Send(ReadOnlySpan<byte> data)
	{
		if (Disposed)
			throw new ObjectDisposedException(GetType().FullName);

		Send(Method.Data, data);
	}

	/// <summary>
	/// Sends a disconnect notice and closes the connection
	/// </summary>
	/// <exception cref="ObjectDisposedException"></exception>
	public void Disconnect()
	{
		if (Disposed)
			throw new ObjectDisposedException(GetType().FullName);

		CloseTokenSource.Cancel();
		Send(Method.Disconnect);
		Connected = false;
	}

	/// <summary>
	/// Closes the connection
	/// </summary>
	/// <exception cref="ObjectDisposedException"></exception>
	public void Close()
	{
		if (Disposed)
			throw new ObjectDisposedException(GetType().FullName);

		CloseTokenSource.Cancel();
		Connected = false;

		OnClose();
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


	/// <summary>
	/// Called when a connection is successfully established with the server
	/// </summary>
	protected abstract void OnConnect();

	/// <summary>
	/// Called when data is received from the server
	/// </summary>
	/// <param name="data"></param>
	protected abstract void OnReceive(ReadOnlySpan<byte> data);

	/// <summary>
	/// Called when a server disconnect notice is received
	/// </summary>
	protected abstract void OnDisconnect();

	/// <summary>
	/// Called when the connection is closed
	/// </summary>
	protected abstract void OnClose();


	private async void Update()
	{
		try
		{
			while (!CloseTokenSource.IsCancellationRequested)
			{
				if (Connected)
					Send(Method.KeepAlive);
				else
					Send(Method.Connect);

				await Task.Delay(1000, CloseTokenSource.Token);
			}
		}
		catch (OperationCanceledException) { }
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
			while (!CloseTokenSource.IsCancellationRequested)
			{
#if NET7_0
				SocketReceiveFromResult receiveResult = await Socket.ReceiveFromAsync(ReceiveBuffer, SocketFlags.None, ServerEndPoint, CloseTokenSource.Token);
#else
				SocketReceiveFromResult receiveResult = await Socket.ReceiveFromAsync(ReceiveBuffer, SocketFlags.None, ServerEndPoint);
#endif

#if NET7_0
				Memory<byte> data = ReceiveBuffer[..receiveResult.ReceivedBytes];
#else
				Memory<byte> data = new Memory<byte>(ReceiveBuffer.Array).Slice(0, receiveResult.ReceivedBytes);
#endif

				if (data.Length <= 0)
					return;

				ReadOnlyMemory<byte> methodData = data.Slice(1);

				lock (CloseTokenSource)
				{
					if (CloseTokenSource.IsCancellationRequested)
						return;

					switch ((Server.Method)data.Span[0])
					{
						case Server.Method.Data:
							OnReceive(methodData.Span);
							break;

						case Server.Method.AcceptConnect:
							OnConnect();
							Connected = true;
							break;

						case Server.Method.Reconnect:
							Connected = false;
							break;

						case Server.Method.Disconnect:
							OnDisconnect();
							Close();
							break;
					}
				}
			}
		}
		catch (OperationCanceledException) { }
	}

	private void SendRaw(ReadOnlySpan<byte> data)
	{
#if NET7_0
		Socket.SendTo(data, SocketFlags.None, ServerEndPoint);
#else
		Socket.SendTo(data.ToArray(), SocketFlags.None, ServerEndPoint);
#endif
	}

	private void Send(Method method) =>
		SendRaw(new byte[] { (byte)method });

	private void Send(Method method, ReadOnlySpan<byte> data)
	{
		Span<byte> methodData = new byte[data.Length + 1];
		methodData[0] = (byte)method;
		data.CopyTo(methodData.Slice(1));
		SendRaw(methodData);
	}
}