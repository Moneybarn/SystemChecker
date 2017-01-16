using Microsoft.Extensions.Logging;
using Quartz;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SystemChecker.Model.Data;
using SystemChecker.Model.Data.Interfaces;
using SystemChecker.Model.Helpers;
using Quartz.Impl.Matchers;

namespace SystemChecker.Model.Scheduling
{
    public class ScheduleUpdater : IJob
    {
        //private static readonly List<CheckTrigger> CheckTriggers = new List<CheckTrigger>();
        private static bool _running = false;
        private static DateTime _lastRun = DateTime.Now;

        private ILogger _logger;
        private IRepositoryFactory _repoFactory;
        private IScheduler _sched;
        private ICheckToPerformRepository _checkToPerformRepo;
        private ICheckTriggerRepository _triggerRepository;
        private ICheckResultRepository _resultsRepo;

        public int CheckToPerformId { get; set; }

        public Task Execute(IJobExecutionContext context)
        {
            if (!_running)
            {
                _running = true;

                try
                {
                    _sched = context.Scheduler;
                    _repoFactory = _sched.Context["RepositoryFactory"] as IRepositoryFactory;
                    _logger = _sched.Context["Logger"] as ILogger;

                    if (_repoFactory == null)
                        throw new ArgumentException("Repository Factory missing from job context");

                    // use the repo factory to get some repos
                    _checkToPerformRepo = _repoFactory.GetCheckToPerformRepository();
                    _triggerRepository = _repoFactory.GetCheckTriggerRepository();
                    _resultsRepo = _repoFactory.GetCheckResultRepository();

                    UpdateJobs(context);
                }
                catch (Exception e)
                {
                    JobExecutionException e2 =
                        new JobExecutionException(e);

                    throw e2;
                }
                finally
                {
                    _running = false;
                }

            }
            else
            {
                var logger = context.Scheduler.Context["Logger"] as ILogger;
                logger.LogWarning("Not running update check as already running!");
            }

            _lastRun = DateTime.Now;

            return null;
        }

        private void UpdateJobs(IJobExecutionContext context)
        {
            // Get current list of enabled checks from db
            var activeJobsFromDb = _checkToPerformRepo.GetEnabledChecks();

            // what jobs do we currently have in the system?
            var currentJobKeysDictionary = GetCurrentJobKeysDictionary();

            // remove any jobs we have running, which no longer appear in the above list
            var jobsToRemove =
                currentJobKeysDictionary
                .Where(x => !activeJobsFromDb.Select(y => "Check " + y.CheckId).Contains(x.Key))
                .Select(x => x.Key)
                .ToList();

            // or have been changed since we last ran (we will simply remove & readd these)
            jobsToRemove.AddRange(
                activeJobsFromDb
                    .Where(y => y.Updated > _lastRun)
                    .Select(x => "Check " + x.CheckId)
                    .ToList());

            foreach (var jobKeyIdentifier in jobsToRemove)
            {
                _logger.LogInformation("Removing job " + jobKeyIdentifier);


                var jobKey = currentJobKeysDictionary[jobKeyIdentifier];

                if (jobKey != null)
                {
                    _sched.DeleteJob(jobKey);
                    currentJobKeysDictionary.Remove(jobKeyIdentifier);
                }
                else
                {
                    _logger.LogWarning("Unable to find job to remove!");
                }
            }

            // have any checks been added since we last checked
            var checksToAdd =
                activeJobsFromDb.Where(x => !currentJobKeysDictionary.ContainsKey("Check " + x.CheckId)).ToList();

            foreach (var check in checksToAdd)
            {
                _logger.LogInformation("Check ID " + check.CheckId + " added: " + check.SystemName);

                // setup a the schedule for this check...
                IJobDetail job = JobBuilder.Create<ScheduledCheckRunner>()
                    .WithIdentity($"Check {check.CheckId}", check.SystemName)
                    .WithDescription(check.SystemName)
                    .UsingJobData("CheckToPerformId", check.CheckId)
                    .StoreDurably()
                    .Build();

                _sched.AddJob(job, false);
            }

            // update the list of current jobs
            currentJobKeysDictionary = GetCurrentJobKeysDictionary();

            // now check the triggers for all of the jobs
            foreach (var jobKey in currentJobKeysDictionary)
            {
                var job = _sched.GetJobDetail(jobKey.Value).Result;
                var check = activeJobsFromDb.FirstOrDefault(x => x.CheckId == job.JobDataMap.GetInt("CheckToPerformId"));

                if (job != null)
                {
                    UpdateTriggersForCheck(check, job);
                }
                else
                {
                    _logger.LogWarning("Unable to find job to update triggers!");
                }
            }
        }

        private void UpdateTriggersForCheck(CheckToPerform check, IJobDetail job)
        {
            var activeTriggersFromDb = _triggerRepository.GetEnabledTriggersForCheckId(check.CheckId);

            var currentTriggerKeysDictionary = GetCurrentTriggerKeysDictionary();

            // what do we have running that doesn't exist in the db
            var triggersToRemove = currentTriggerKeysDictionary
                .Where(x => !activeTriggersFromDb.Any(y => $"Trigger {y.TriggerId}" == x.Key))
                .ToList();

            foreach (var trigger in triggersToRemove)
            {
                _logger.LogInformation("Trigger " + trigger.Key + " removed");
                
                _sched.UnscheduleJob(trigger.Value);
                currentTriggerKeysDictionary.Remove(trigger.Key);
            }

            // any triggers which have changed?
            var changedTriggers = activeTriggersFromDb.Where(x => x.Updated > _lastRun).ToList();
            foreach (var trigger in changedTriggers)
            {
                _logger.LogInformation("Trigger ID " + trigger.TriggerId + " updated for " + check.SystemName + ": " + trigger.CronExpression);

                var newTrigger = BuildTrigger(_resultsRepo, check, trigger, job);
                var triggertoRemove = _sched.GetTriggerFromTriggerId(check.CheckId, trigger.TriggerId);
                if (triggertoRemove != null)
                {
                    _sched.RescheduleJob(triggertoRemove.Key, newTrigger);
                }
                else
                {
                    _sched.ScheduleJob(newTrigger);
                    _logger.LogWarning("Could not find old trigger to update! Adding new trigger anyway..");
                }
            }

            // whats in the db which doesn't already exist in our known dictionary
            var newTriggers = activeTriggersFromDb
                .Where(x => currentTriggerKeysDictionary.ContainsKey($"Trigger {x.TriggerId}"))
                .ToList();

            foreach (var trigger in newTriggers)
            {
                _logger.LogInformation("Trigger ID " + trigger.TriggerId + " added for " + check.SystemName + ": " + trigger.CronExpression);

                var newTrigger = BuildTrigger(_resultsRepo, check, trigger, job);
                _sched.ScheduleJob(newTrigger);
            }
        }

        private Dictionary<string, JobKey> GetCurrentJobKeysDictionary()
        {
            var groupMatcher = GroupMatcher<JobKey>.AnyGroup();
            return _sched.GetJobKeys(groupMatcher).Result.ToDictionary(key => key.Name);
        }

        private Dictionary<string, TriggerKey> GetCurrentTriggerKeysDictionary()
        {
            var groupMatcher = GroupMatcher<TriggerKey>.AnyGroup();
            return _sched.GetTriggerKeys(groupMatcher).Result.ToDictionary(key => key.Name);
        }

        private ITrigger BuildTrigger(ICheckResultRepository resultsRepo, CheckToPerform check, CheckTrigger trigger, IJobDetail job)
        {
            DateTimeOffset startAt;

            if (trigger.PerformCatchUp)
            {
                var lastRun = resultsRepo.GetLastRunByCheckId(check.CheckId);
                startAt = lastRun == null
                    ? new DateTimeOffset(DateTime.Now)
                    : new DateTimeOffset(lastRun.CheckDTS);
            }
            else
            {
                startAt = new DateTimeOffset(DateTime.Now);
            }

            return TriggerBuilder.Create()
                .WithIdentity($"Trigger {trigger.TriggerId}", $"Check {check.CheckId}" )
                .StartAt(startAt.AddSeconds(1)) // fixes firing twice when updating
                .WithCronSchedule(trigger.CronExpression, x => x.WithMisfireHandlingInstructionFireAndProceed())
                .UsingJobData("CheckTriggerId", trigger.TriggerId)
                .ForJob(job)
                .Build();
        }
    }
}