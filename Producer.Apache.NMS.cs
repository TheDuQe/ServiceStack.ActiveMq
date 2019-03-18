using Apache.NMS;
using ServiceStack.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceStack.ActiveMq
{
	internal partial class Producer : IDisposable
	{
		internal CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

		public event EventHandler<System.Data.ConnectionState> ConnectionStateChanged;

		private System.Data.ConnectionState _state = System.Data.ConnectionState.Closed;
		public System.Data.ConnectionState State
		{
			get
			{
				return _state;
			}
			internal set
			{
				bool raise = _state != value;
				System.Data.ConnectionState oldstate = _state;
				_state = value;
				if (raise)
				{
					ActiveMqExtensions.Logger.Debug($"Active MQ Connector [{this.ConnectionName}] has changed from [{oldstate.ToString()}] to [{_state.ToString()}]");
					ConnectionStateChanged?.Invoke(this, value);
				}
			}
		}

		protected Apache.NMS.DestinationType QueueType = Apache.NMS.DestinationType.Queue;

		public string ConnectionName { get; private set; }

		public Apache.NMS.ISession Session { get; internal set; }

		private IConnection connection;
		public IConnection Connection
		{
			get
			{
				if (connection == null)
				{
					connection = ((MessageFactory)MessageFactory).GetConnectionAsync().Result;
				}
				return connection;
			}
			internal set
			{
				connection = value;
			}
		}

		internal void ConnectAsync()
		{
			if (this.Connection.IsStarted) return;

			string stage = "Entering method OpenAsync";
			this.State = System.Data.ConnectionState.Connecting;
			try
			{
				ActiveMqExtensions.Logger.Info($"Connecting ActiveMQ Broker... [{this.Connection.ClientId}]");

				this.Connection.Start();
				this.ConnectionName = this.Connection.ClientId;
				this.Connection.ConnectionInterruptedListener += Connection_ConnectionInterruptedListener;
				this.Connection.ConnectionResumedListener += Connection_ConnectionResumedListener;
				this.Connection.ExceptionListener += Connection_ExceptionListener;

				this.State = System.Data.ConnectionState.Open;

				stage = $"starting session to broker [{this.Connection.Dump()}]";

			}
			catch (TaskCanceledException)
			{
				ActiveMqExtensions.Logger.Info($"Safe connection close for {this.Connection.ClientId} while {stage});");
			}
			catch (Exception ex)
			{
				ActiveMqExtensions.Logger.Error($"An exception has occured while {stage} for {this.Connection.Dump()} : {ex.Dump()});");
			}
		}

		internal void CloseConnection()
		{
			ActiveMqExtensions.Logger.Warn($"Connection to ActiveMQ (Queue : {this.ConnectionName} is shutting down");

			if (connection != null)
			{
				connection.Stop();

				connection.ConnectionInterruptedListener -= Connection_ConnectionInterruptedListener;
				connection.ConnectionResumedListener -= Connection_ConnectionResumedListener;
				connection.ExceptionListener -= Connection_ExceptionListener;
				//Close all MQClient queues !!!! Not implemented
				connection.Close();
			}


		}

		private void Connection_ExceptionListener(Exception exception)
		{
			this.OnMessagingError(exception);
		}

		private void Connection_ConnectionResumedListener()
		{
			///var consumer = this.Consumer; //Better to switch off listening and recreates when reconnect
			ActiveMqExtensions.Logger.Info($"Connection to [{this.ConnectionName}] has been restored ");
			this.State = System.Data.ConnectionState.Open;
		}

		private void Connection_ConnectionInterruptedListener()
		{
			//_consumer.Dispose(); //Better to switch off listening and recreates when reconnect
			//_consumer = null;
			this.State = System.Data.ConnectionState.Broken;
			this.OnTransportError(new Apache.NMS.NMSConnectionException($"Connection to broker has been interrupted"));
		}

		internal async Task OpenSessionAsync()
		{
			this.ConnectAsync();
			if (this.Session != null)
			{
				dynamic session = this.Session;
				if (session.Started) return;
			}

			using (this.Session = this.Connection.CreateSession())
			{
				if (this.IsConsumer) // QueueClient
				{
					this.State = System.Data.ConnectionState.Fetching;
				}
				else // Producer
				{
					this.State = System.Data.ConnectionState.Executing;
				}

				while (!this.cancellationTokenSource.IsCancellationRequested) // Checks every half second wether Session must be closed
				{
					await Task.Delay(500, this.cancellationTokenSource.Token);
				}
			}
		}

		private string RemoveLineEndings(string value)
		{
			if (String.IsNullOrEmpty(value))
			{
				return value;
			}
			string lineSeparator = ((char)0x2028).ToString();
			string paragraphSeparator = ((char)0x2029).ToString();

			return value.Replace("\r\n", string.Empty)
						.Replace("\n", string.Empty)
						.Replace("\r", string.Empty)
						.Replace("\t", string.Empty)
						.Replace(lineSeparator, string.Empty)
						.Replace(paragraphSeparator, string.Empty);
		}


		#region Consumer
		/// <summary>
		// Turn received Message into expected type of the ServiceStackMessageHandler<T>
		/// </summary>
		/// <param name="apsession"></param>
		/// <param name="producer"></param>
		/// <param name="message"></param>
		/// <returns>An Apache.NMS.IObjectMessage</returns>
		internal Apache.NMS.ConsumerTransformerDelegate CreateConsumerTransformer()
		{
			return new Apache.NMS.ConsumerTransformerDelegate((apsession, consumer, message) =>
			{
				try
				{
					Apache.NMS.IObjectMessage objMessage = message as IObjectMessage;
					if (objMessage == default(IObjectMessage))
					{
						ITextMessage txtMessage = message as Apache.NMS.ITextMessage;
						if (txtMessage != default(Apache.NMS.ITextMessage))
						{
							string incomingmessage = RemoveLineEndings(txtMessage.Text);
							incomingmessage = System.Text.RegularExpressions.Regex.Unescape(incomingmessage.Trim('"'));
							//incomingmessage = incomingmessage.ToSafeJson();

							////incomingmessage = 
							//// necessary for ServiceStack to deserialize
							//object obj = JsonSerializer.DeserializeFromString(incomingmessage, this.MessageHandler.MessageType);
							object obj = Newtonsoft.Json.JsonConvert.DeserializeObject(incomingmessage, this.MessageHandler.MessageType);
							objMessage = this.Session.CreateObjectMessage(obj);
							objMessage.PopulateWithNonDefaultValues(message);
						}
						else
						{
							Apache.NMS.IBytesMessage msgBytes = message as Apache.NMS.IBytesMessage;
							if (msgBytes == default(Apache.NMS.IBytesMessage))
							{
								msgBytes.Reset();
								string output = System.Text.Encoding.UTF8.GetString(msgBytes.Content);
								// TODO : Convert to Apache.NMS.IObjectMessage
							}
						}
					}


					this.TotalNormalMessagesReceived++;
					if (message.NMSPriority == MsgPriority.AboveNormal) this.TotalPriorityMessagesReceived++;

					if (objMessage == null)
					{
						throw new InvalidOperationException("Object Message is null after parsing as IObjectMessage or ITextMessage or IBytesMessage");
					}
					return objMessage;
				}
				catch (Exception ex)
				{
					// Will provoke a Nack message by Apache.NMS
					ex = new MessageNotReadableException($"Message [{message.Dump()}] could not be parsed as a valid Apache.NMS.IObjectMessage of type {this.MessageHandler.MessageType.Name}");
					this.OnMessagingError(ex);
				}
				return this.Session.CreateObjectMessage(Activator.CreateInstance(this.MessageHandler.MessageType));
			});
		}

		protected static SemaphoreSlim semaphoreConsumer = null;
		Apache.NMS.IMessageConsumer _consumer = null;
		internal Apache.NMS.IMessageConsumer Consumer
		{
			get
			{
				if (_consumer == null)
				{
					string queuename = this.ResolveQueueNameFn(MessageHandler.MessageType.Name, ".inq");
					this.QueueName = queuename;
					Apache.NMS.IDestination destination = null;
					try
					{
						semaphoreConsumer.Wait();
						destination = Apache.NMS.Util.SessionUtil.GetDestination(this.Session, queuename);
						_consumer = this.Session.CreateConsumer(destination);
						_consumer.ConsumerTransformer = CreateConsumerTransformer();

						ActiveMqExtensions.Logger.Debug($"A Consumer {_consumer.Dump()} has been created to listen on queue [{queuename}].");
					}
					catch (Exception ex)
					{
						ActiveMqExtensions.Logger.Warn($"A problem occured while creating a Consumer on queue [{queuename}]: {ex.GetBaseException().Message}");
						throw;
					}
					finally
					{
						semaphoreConsumer.Release();
					}
				}
				return _consumer;
			}
		}

		public string QueueName { get; private set; }

		#endregion

		#region Producer


		internal Apache.NMS.ProducerTransformerDelegate CreateProducerTransformer()
		{
			return new ProducerTransformerDelegate((session, producer, message) =>
			{
				IObjectMessage obj = message as IObjectMessage;
				//TRW : 24/09/2018 > TRW Recast as ITextMessage
				//obj.Body = JsonSerializer.SerializeToString(obj.Body);
				//return obj;
				string serialized = JsonSerializer.SerializeToString(obj.Body);
				serialized = System.Text.RegularExpressions.Regex.Unescape(serialized.Trim('"'));
				ITextMessage messageToPost = this.Session.CreateTextMessage(serialized);
				return messageToPost;
			});
		}

		static SemaphoreSlim semaphoreProducer = null;

		internal async Task<Apache.NMS.IMessageProducer> GetProducer(string queuename)
		{
			this.QueueName = queuename;
			Apache.NMS.IMessageProducer _producer = null;
			IDestination destination = null;
			try
			{
				if (!this.cancellationTokenSource.IsCancellationRequested)
				{
					await OpenSessionAsync();
					semaphoreProducer.Wait();
					destination = ((Apache.NMS.ActiveMQ.Session)this.Session).GetDestination(queuename);

					_producer = this.Session.CreateProducer(destination);
					if (_producer != default(Apache.NMS.IMessageProducer)) _producer.ProducerTransformer = CreateProducerTransformer();

					return _producer;
				}
				else
				{
					return null;
				}
			}
			catch (Exception ex)
			{
				ActiveMqExtensions.Logger.Warn($"A problem occured while creating a Producer on queue {queuename}: {ex.GetBaseException().Message}");
				return null;
			}
			finally
			{
				semaphoreProducer.Release();
			}
		}
		#endregion

		#region IDisposable Support
		private bool disposedValue = false; // To detect redundant calls

		protected virtual void Dispose(bool disposing)
		{
			if (!disposedValue)
			{
				if (disposing)
				{
					ActiveMqExtensions.Logger.Info($"Close connection : [{this.ConnectionName}]");
					// Close Listening Thread
					cancellationTokenSource.Cancel();
					//Wait until Cancellation has been achieved
					Task.Delay(500);

					this.CloseConnection();
					if (connection != default(IConnection))
					{
						connection.Dispose();
						connection = null;
					}
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
