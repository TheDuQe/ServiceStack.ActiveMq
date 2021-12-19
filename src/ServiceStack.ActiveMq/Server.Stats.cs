using ServiceStack.Messaging;
using ServiceStack.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServiceStack.ActiveMq
{
	public partial class Server
	{
		public virtual IMessageHandlerStats GetStats()
		{
			lock (this.Workers)
			{
				var total = new MessageHandlerStats("All Handlers");
				this.Workers.ToList().ForEach(x => 
							total.Add(x.GetStats())
							);
				return total;
			}
		}

		public virtual string GetStatus()
		{
			return WorkerStatus.ToString(5);
		}

		public virtual string GetStatsDescription()
		{
			//lock (this.Workers)
			//{
				var sb = StringBuilderCache.Allocate().Append("#MQ SERVER STATS:\n");
				//sb.AppendLine("===============");
				//sb.AppendLine("Current Status: " + GetStatus());
				//sb.AppendLine("Listening On: " + string.Join(", ", this.Workers.ToList().ConvertAll(x => x.MQClient.ToString()).ToArray()));
				//sb.AppendLine("Times Started: " + System.Threading.Interlocked.CompareExchange(ref timesStarted, 0, 0));
				//sb.AppendLine("Num of Errors: " + System.Threading.Interlocked.CompareExchange(ref noOfErrors, 0, 0));
				//sb.AppendLine("Num of Continuous Errors: " + System.Threading.Interlocked.CompareExchange(ref noOfContinuousErrors, 0, 0));
				//sb.AppendLine("Last ErrorMsg: " + lastExMsg);

				sb.AppendLine("===============");
				foreach (var worker in this.Workers)
				{
					sb.AppendLine(worker.GetStats().ToString());
					sb.AppendLine("---------------\n");
				}
				return StringBuilderCache.ReturnAndFree(sb);
			//}
		}


	}
}
