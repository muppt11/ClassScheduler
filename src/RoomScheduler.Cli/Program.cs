using System;
using System.Linq;

namespace RoomScheduler
{
    public class Program
    {
        /// <summary>
        /// Console version of the semester planner. "plan" in front is optional:
        /// RoomScheduler requests.xlsx --history past.xlsx --out schedule.xlsx
        /// </summary>
        public static void Main(string[] args)
        {
            PlanCommand.Run(args.Length > 0 && args[0] == "plan" ? args.Skip(1).ToArray() : args);
        }
    }
}
