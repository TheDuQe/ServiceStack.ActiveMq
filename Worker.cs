using ServiceStack.Logging;
using ServiceStack.Messaging;
using System;
using System.Threading.Tasks;

namespace ServiceStack.ActiveMq
{
	internal class Worker
	{
		/// <summary>
		/// Creates and start a worker
		/// </summary>
		/// <param name="service"></param>
		/// <param name="handlerFactory"></param>
		/// <returns></returns>
		internal static async Task<Worker> StartAsync(Server service, ServiceStack.Messaging.IMessageHandlerFactory handlerFactory)
		{
			Worker client = new Worker(handlerFactory, service.MessageFactory, service.ErrorHandler);
			await client.Dequeue();
			return client;
		}


		private Worker(ServiceStack.Messaging.IMessageHandlerFactory handlerFactory, ServiceStack.Messaging.IMessageFactory messageFactory, Action<Worker, Exception> errorHandler)
		{
			this.messageFactory = messageFactory;
			this.messageHandlerFactory = handlerFactory;
			this.ErrorHandler = errorHandler;
		}

		private static readonly ILog Log = LogManager.GetLogger(typeof(Worker));

		internal readonly ServiceStack.Messaging.IMessageHandlerFactory messageHandlerFactory;
		internal readonly ServiceStack.Messaging.IMessageFactory messageFactory;

		internal Action<Worker, Exception> ErrorHandler { get; set; }

		ServiceStack.Messaging.IMessageQueueClient client = null;
		internal ServiceStack.Messaging.IMessageQueueClient MQClient
		{
			get
			{
				if (client == null)
				{
					client = this.messageFactory.CreateMessageQueueClient();
					((QueueClient)client).MessageFactory = messageFactory;
					((QueueClient)client).MessageHandler = messageHandlerFactory.CreateMessageHandler();

				}
				return client;
			}
		}

		internal async Task Dequeue(long messagesCount = long.MaxValue, TimeSpan? timeOut = null)
		{
			if (!timeOut.HasValue) timeOut = System.Threading.Timeout.InfiniteTimeSpan;
			var queue = ((QueueClient)this.MQClient);

			// Open connection and once opened (Server might be unavailable)...
			await queue.StartAsync().ContinueWith(tsk =>
			{
				try
				{
					// Start to dequeue
					Task.Factory.StartNew(()=>queue.MessageHandler.ProcessQueue(queue, queue.QueueName, () => GetStats().TotalMessagesProcessed <= messagesCount));
				}
				catch (Exception ex)
				{
					Log.Error(new OperationCanceledException($"The is queue [{queue.QueueName}] might not work as an exception occured while listening", ex));
					ErrorHandler?.Invoke(this, ex);
				}
			});


		}


		#region IDisposable Members

		private bool isDisposed = false;
		public void Dispose()
		{
			if (!this.isDisposed)
			{
				this.MQClient.Dispose();
				this.messageFactory.Dispose();
				this.isDisposed = true;
			}
		}

		public void Add(IMessageHandlerStats stats)
		{
			throw new NotImplementedException();
		}

		#endregion

		public virtual IMessageHandlerStats GetStats()
		{
			return ((Producer)this.MQClient).MessageHandler.GetStats();
		}

		public virtual string GetStatus()
		{
			return $"[Worker: {((Producer)this.MQClient).QueueName}, LastMsgAt: {((Producer)this.MQClient).LastMessageProcessed}]";
		}

	}
}


