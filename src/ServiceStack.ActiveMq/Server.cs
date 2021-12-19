using ServiceStack.Logging;
using ServiceStack.Messaging;
using ServiceStack.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ServiceStack.ActiveMq
{
	public partial class Server : IMessageService, IDisposable
	{

		public int DefaultRetryCount = (new Apache.NMS.Policies.RedeliveryPolicy()).MaximumRedeliveries; //Will be a total of 2 attempts

		public Server(
		string connectionString = "tcp://localhost:61616",
		string username = null,
		string password = null) :
	this(new ActiveMq.MessageFactory(connectionString, username, password))
		{

		}

		public Server(ActiveMq.MessageFactory messageFactory)
		{
			
			this.messageFactory = messageFactory;
			this.ResolveQueueNameFn = (type, suffix) => ServiceStack.Messaging.QueueNames.ResolveQueueNameFn(type as string, suffix);
			this.ErrorHandler = (worker, ex) => ActiveMqExtensions.Logger.Error("Exception in Active MQ Plugin: ", ex);

		}



		/// <summary>
		/// Execute global transformation or custom logic before a request is processed.
		/// Must be thread-safe.
		/// </summary>
		public Func<IMessage, IMessage> RequestFilter { get; set; }

		/// <summary>
		/// Execute global transformation or custom logic on the response.
		/// Must be thread-safe.
		/// </summary>
		public Func<object, object> ResponseFilter { get; set; }

		internal IEnumerable<Worker> Workers
		{
			get
			{
				return handlerMap.Values.SelectMany(item => item.Item2);
			}
		}

		public bool isConnected
		{
			get
			{
				return Workers.Any(worker => ((MessageFactory)worker.messageFactory).isConnected());
			}
		}

		public bool isStarted
		{
			get
			{
				return Workers.Any(worker => ((MessageFactory)worker.messageFactory).isStarted());
			}
		}

		public bool isFaultTolerant
		{
			get
			{
				return Workers.Any(worker => ((MessageFactory)worker.messageFactory).isFaultTolerant());
			}
		}

		public int RetryCount { get; set; }

		public Action<string, Apache.NMS.IPrimitiveMap, IMessage> PublishMessageFilter
		{
			get { return messageFactory.PublishMessageFilter; }
			set { messageFactory.PublishMessageFilter = value; }
		}

		public Action<string, ServiceStack.Messaging.IMessage> GetMessageFilter
		{
			get { return messageFactory.GetMessageFilter; }
			set { messageFactory.GetMessageFilter = value; }
		}

		public Func<object, string, string> ResolveQueueNameFn
		{
			get { return messageFactory.ResolveQueueNameFn; }
			set { messageFactory.ResolveQueueNameFn = value; }
		}

		public Action<string, Dictionary<string, object>> CreateQueueFilter { get; set; }
		public Action<string, Dictionary<string, object>> CreateTopicFilter { get; set; }

		protected IMessageHandlerFactory CreateMessageHandlerFactory<T>(Func<IMessage<T>, object> processMessageFn, Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx)
		{
			return new MessageHandlerFactory<T>(this, processMessageFn, processExceptionEx)
			{
				RequestFilter = this.RequestFilter,
				ResponseFilter = this.ResponseFilter,
				//PublishResponsesWhitelist = PublishResponsesWhitelist,
				RetryCount = RetryCount,
			};
		}

		/// <summary>
		/// The Message Factory used by this MQ Server
		/// </summary>
		private MessageFactory messageFactory;
		public IMessageFactory MessageFactory => messageFactory;

		/// <summary>
		/// Execute global error handler logic. Must be thread-safe.
		/// </summary>
		internal Action<Worker, Exception> ErrorHandler { get; set; }

		public List<Type> RegisteredTypes => handlerMap.Keys.ToList();

		private readonly Dictionary<Type, Tuple<IMessageHandlerFactory, Worker[]>> handlerMap = new Dictionary<Type, Tuple<IMessageHandlerFactory, Worker[]>>();

		public void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn)
		{
			RegisterHandler(processMessageFn, null, noOfThreads: -1);
		}

		public void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn, int noOfThreads)
		{
			RegisterHandler(processMessageFn, null, noOfThreads);
		}

		public void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn, Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx)
		{
			RegisterHandler(processMessageFn, processExceptionEx, noOfThreads: -1);
		}

		public void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn, Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx, int noOfThreads)
		{
			if (handlerMap.ContainsKey(typeof(T)))
				throw new ArgumentException("Message handler has already been registered for type: " + typeof(T).Name);
			//Checl licence validity before instantiating anything
			LicenseUtils.AssertValidUsage(LicenseFeature.ServiceStack, QuotaType.Operations, handlerMap.Count + 1);

			if (noOfThreads <= 0) noOfThreads = 1; // 1 is Best performances as Apache.NMS manages threading
			IMessageHandlerFactory handlerMessageFactory = CreateMessageHandlerFactory<T>(processMessageFn, processExceptionEx);
			handlerMap[typeof(T)] = new Tuple<IMessageHandlerFactory, Worker[]>(handlerMessageFactory, new Worker[noOfThreads]);

			ActiveMqExtensions.Logger.Info($"Message of type [{typeof(T).Name}] has been registered to use with ActiveMq");
		}

		public void Start()
		{
			handlerMap.Select(kv => kv.Value)
				.ToList()
				.ForEach(async tuple =>
				{
					//Log.Info(($"Start ActiveMq Messages handler for type {kv.}")
					for (int i = 0; i < tuple.Item2.Length; i++)
					{
						ActiveMqExtensions.Logger.Info($"Start worker [{i+1}] for type [{tuple.Item1}] ");
						tuple.Item2[i] = await Worker.StartAsync(this, tuple.Item1);
						await tuple.Item2[i].Dequeue();
					}
				});
			ServiceStack.HostContext.AppHost.Register<IMessageService>(this);
		}

		static Random rnd = new Random();

		/// <summary>
		/// Used for SendOneWay client which does not register an MQClient
		/// </summary>
		private string QueueOut { get; set; }

		public async Task<bool[]> SendAllAsync<T>(IEnumerable<T> messages)
		{
			return await Task.WhenAll(messages.ToList().Select(async msg => await SendAsync(msg)).ToArray());
		}

		public async Task<bool> SendAsync<T>(T message)
		{
			if (!handlerMap.ContainsKey(typeof(T))) this.ErrorHandler(null, new InvalidOperationException($"No ServiceStack.ActiveMQ server has been registered for type {typeof(T).Name}"));
			if (handlerMap[typeof(T)].Item2.Length == 0) this.ErrorHandler(null, new InvalidOperationException($"No ServiceStack.ActiveMQ worker has been registered for type {typeof(T).Name}"));
			if (string.IsNullOrEmpty(QueueOut)) { QueueOut = this.ResolveQueueNameFn(message, ".outq"); }

			return await Task.Factory.StartNew<bool>(() =>
			{
				try
				{
					int workerNumber = rnd.Next(handlerMap[typeof(T)].Item2.Length);
					Worker worker = handlerMap[typeof(T)].Item2[workerNumber];
					worker.MQClient.Publish<T>(message);
					return true;
				}
				catch (Exception ex)
				{
					this.ErrorHandler(null, new InvalidOperationException($"An error occured while sending message of type {typeof(T).Name}", ex));
				}
				return false;
			});
		}

		public void Stop()
		{
			this.Workers.ToList().ForEach(
				worker =>
				{
					if (worker != null)//Warning worker can be null if appHost has been disposed, but no callback was added to dispose plugin
					{
						worker.Dispose();
						worker = null;
					}
				}
			);
		}

		#region IDisposable Support
		private bool disposedValue = false; // To detect redundant calls

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					// Close Listening Thread
					this.messageFactory.Dispose();
				}
				// TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
				// TODO: set large fields to null.
				disposedValue = true;
			}
		}

		// TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
		// ~Producer() {
		//   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
		//   Dispose(false);
		// }

		// This code added to correctly implement the disposable pattern.
		public void Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
			// TODO: uncomment the following line if the finalizer is overridden above.
			GC.SuppressFinalize(this);
			GC.Collect();
		}
		#endregion


	}
}
