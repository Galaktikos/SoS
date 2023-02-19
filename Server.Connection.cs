using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SoS;

public abstract partial class Server
{
	public abstract class Connection : IDisposable
	{
		internal Server? Server = null;
		internal EndPoint? EndPoint = null;

		/// <summary>
		/// Send the specified <paramref name="data"/> to the client
		/// </summary>
		/// <param name="data"></param>
		/// <exception cref="ObjectDisposedException"></exception>
		/// <exception cref="NullReferenceException"></exception>
		public void Send(ReadOnlySpan<byte> data)
		{
			if (Disposed)
				throw new ObjectDisposedException(GetType().FullName);

			lock (Server ?? throw new NullReferenceException(nameof(Server)))
				lock (EndPoint ?? throw new NullReferenceException(nameof(EndPoint)))
					Server.SendTo(EndPoint, Method.Data, data);
		}

		/// <summary>
		/// Sends a disconnect notice and closes the connection
		/// </summary>
		/// <exception cref="ObjectDisposedException"></exception>
		/// <exception cref="NullReferenceException"></exception>
		public void Disconnect()
		{
			if (Disposed)
				throw new ObjectDisposedException(GetType().FullName);

			lock (Server ?? throw new NullReferenceException(nameof(Server)))
				lock (EndPoint ?? throw new NullReferenceException(nameof(EndPoint)))
					Server.SendTo(EndPoint, Method.Disconnect);

			Close();
		}

		/// <summary>
		/// Closes the connection
		/// </summary>
		/// <exception cref="ObjectDisposedException"></exception>
		/// <exception cref="NullReferenceException"></exception>
		public void Close()
		{
			if (Disposed)
				throw new ObjectDisposedException(GetType().FullName);

			KeepaliveTokenSource?.Cancel();

			IPEndPoint ipEndPoint;
			lock (EndPoint ?? throw new NullReferenceException(nameof(EndPoint)))
				ipEndPoint = (IPEndPoint)EndPoint;

			lock (Server ?? throw new NullReferenceException(nameof(Server)))
				Server.RemoveConnection(new IPPort(ipEndPoint.Address, ipEndPoint.Port));

			OnClose();
		}

		private bool Disposed = false;
		public void Dispose()
		{
			if (Disposed)
				return;

			KeepaliveTokenSource?.Dispose();

			Disposed = true;
			GC.SuppressFinalize(this);
		}


		/// <summary>
		/// Called when data is received from the client
		/// </summary>
		/// <param name="data"></param>
		protected internal abstract void OnReceive(ReadOnlySpan<byte> data);

		/// <summary>
		/// Called when the client disconnect notice is received
		/// </summary>
		protected internal abstract void OnDisconnect();

		/// <summary>
		/// Called when the client times out
		/// </summary>
		protected abstract void OnTimeout();

		/// <summary>
		/// Called when the connection is closed
		/// </summary>
		protected abstract void OnClose();


		private CancellationTokenSource? KeepaliveTokenSource;
		internal async void KeepaliveTimeout()
		{
			KeepaliveTokenSource?.Cancel();
			CancellationTokenSource cancellationTokenSource = new();
			KeepaliveTokenSource = cancellationTokenSource;

			try { await Task.Delay(10000, cancellationTokenSource.Token); }
			catch (OperationCanceledException) { }

			if (cancellationTokenSource.IsCancellationRequested)
				return;

			OnTimeout();
			Close();
		}
	}
}