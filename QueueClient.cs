using ServiceStack.Messaging;
using ServiceStack.Text;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceStack.ActiveMq
{
	internal class QueueClient : Producer, ServiceStack.Messaging.IMessageQueueClient
	{
		//private string QueueNames
		internal QueueClient(MessageFactory factory) : base(factory)
		{
			semaphoreConsumer = new System.Threading.SemaphoreSlim(1);
		}

		public async Task StartAsync()
		{
			await Task.Factory.StartNew(async () => { await this.OpenSessionAsync(); },
				cancellationTokenSource.Token,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default);
		}

		public virtual void Ack(Messaging.IMessage message)
		{
			if (this.Session.AcknowledgementMode == Apache.NMS.AcknowledgementMode.ClientAcknowledge)
			{
				((Apache.NMS.IMessage)message.Body).Acknowledge();
			}
			ActiveMqExtensions.Logger.Debug($"Message to [{message.Id}] has been acked");
		}

		public virtual void Nak(Messaging.IMessage message, bool requeue, Exception exception = null)
		{
			if (this.Session.AcknowledgementMode == Apache.NMS.AcknowledgementMode.ClientAcknowledge)
			{
				//if(exception!=null)
			}
			ActiveMqExtensions.Logger.Debug($"Message to [{message.Id}] has been Nacked");
		}

		public ServiceStack.Messaging.IMessage<T> CreateMessage<T>(object mqResponse)
		{
			return ((Apache.NMS.IObjectMessage)mqResponse).ToMessage<T>();
		}

		//Handles a single message then return
		public ServiceStack.Messaging.IMessage<T> Get<T>(string queueName, TimeSpan? timeSpanOut)
		{
			if (!timeSpanOut.HasValue) timeSpanOut = Timeout.InfiniteTimeSpan;
			var response = GetAsync<T>(queueName, 1, timeSpanOut.Value);
			if(!this.cancellationTokenSource.IsCancellationRequested)this.cancellationTokenSource.Cancel();
			return response;
		}

		//Handles messages and keep running
		public ServiceStack.Messaging.IMessage<T> GetAsync<T>(string queueName)
		{
			return GetAsync<T>(queueName, long.MaxValue, Timeout.InfiniteTimeSpan);
		}

		/// <summary>
		/// This method should be called asynchronously
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="queueName"></param>
		/// <returns></returns>
		public ServiceStack.Messaging.IMessage<T> GetAsync<T>(string queueName, long maxMessagesCount, TimeSpan timeOut)
		{

			ServiceStack.Messaging.IMessage<T> response = default(ServiceStack.Messaging.IMessage<T>);
			Task.Delay(timeOut).ContinueWith((tsk) => this.cancellationTokenSource.Cancel());
			long loopMessagesProcessed = 0;
			while (!this.cancellationTokenSource.IsCancellationRequested && loopMessagesProcessed <= maxMessagesCount)
			{
				var msg = this.Consumer.Receive(timeOut) as Apache.NMS.IObjectMessage;
				if (msg != null)
				{
					response = CreateMessage<T>(msg);
					if (response != null)
					{
						GetMessageFilter?.Invoke(queueName, response);
						loopMessagesProcessed++;
						this.TotalMessagesProcessed++;
						this.LastMessageProcessed = DateTime.Now;
						return response;
					}

				}
			}

			return default(ServiceStack.Messaging.IMessage<T>);
		}

		public void Notify(string queueName, ServiceStack.Messaging.IMessage message)
		{
			throw new NotImplementedException();
		}

	}
}