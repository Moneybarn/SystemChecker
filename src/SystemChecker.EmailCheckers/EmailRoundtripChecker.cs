﻿using System;
using SystemChecker.Model.Checkers;
using SystemChecker.Model.Data;
using SystemChecker.Model.Data.Interfaces;
using SystemChecker.Model.Enums;
using SystemChecker.Model.Interfaces;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Newtonsoft.Json.Linq;
namespace SystemChecker.EmailCheckers
{
    public class EmailRoundtripCheckerSettings
    {
        public string IMAPServer { get; set; }
        public int IMAPPort { get; set; }
        public string IMAPUsername { get; set; }
        public string IMAPPassword { get; set; }
        public bool IMAPUseSSL { get; set; }
        // use ssl false
        // port 143

        // SMTP
        public string SMTPTestEmailRecipient { get; set; }
        public string SMTPTestEmailSender { get; set; }
        public string SMTPServer { get; set; }
        public int SMTPPort { get; set; }
        public string SMTPUsername { get; set; }
        public string SMTPDomain { get; set; }
        public string SMTPPassword { get; set; }
    }

    public class EmailRoundtripChecker : BaseChecker<EmailRoundtripCheckerSettings>, ISystemCheck
    {
        const string EMAIL_TOKEN_PREFIX = "SCT_"; // system checker token
        const string EMAIL_SUBJECT_PREFIX = "Auto Generated Email From System Checker";

        public CheckResult PerformCheck(ICheckResultRepository resultsRepo, ILogger logger)
        {
            logger.LogDebug($"Starting EMailRoundtripChecker- CheckId {this.CheckToPerformId}");

            var lastRun = GetLastRun(resultsRepo);

            var lastEmailReceived = (double?)null;

            if (!string.IsNullOrWhiteSpace(lastRun?.RunData))
            {
                try
                {
                    var lastRunData = JObject.Parse(lastRun.RunData);

                    lastEmailReceived = FetchTestMail(lastRunData["TestEMailToken"].Value<string>(), logger);
                }
                catch (AuthenticationException ae)
                {
                    logger.LogDebug($"AuthenticationException : {ae}");

                    return new CheckResult
                    {
                        Result = (int)SuccessStatus.UnexpectedErrorDuringCheck,
                        FailureDetail = $"Unable to login to IMAP server : {ae.Message}"
                    };
                }
            }

            var testToken = Math.Abs(Guid.NewGuid().GetHashCode()).ToString();
            var testEmailToken = SendTestMail(testToken);

            var thisRun = new
            {
                TestEMailToken = testEmailToken,
                LastEmailReceived = lastEmailReceived.HasValue,
                LastEmailDeliveryTime = lastEmailReceived ?? 0
            };

            var result = PassStatus(thisRun, resultsRepo);

            return new CheckResult
            {
                Result = (int)result.SuccessStatus,
                FailureDetail = result.Description,
                RunData = result.JsonRunData
            };
        }

        private string SendTestMail(string testEmailToken)
        {
            var emailMessage = new MimeMessage();

            emailMessage.From.Add(new MailboxAddress("", Settings.SMTPTestEmailSender));
            emailMessage.To.Add(new MailboxAddress("", Settings.SMTPTestEmailRecipient));
            emailMessage.Subject = $"{EMAIL_SUBJECT_PREFIX} ({EMAIL_TOKEN_PREFIX}{testEmailToken})";
            emailMessage.Body = new TextPart("plain") { Text = $@"
This is a test email generated by the system checker.  
Please ignore and do not delete. 

({EMAIL_TOKEN_PREFIX}{testEmailToken})" };

            using (var client = new SmtpClient())
            {
                client.LocalDomain = Settings.SMTPDomain;
                client.AuthenticationMechanisms.Remove("XOAUTH2");
                client.Authenticate(Settings.SMTPUsername, Settings.SMTPPassword);
                client.ConnectAsync(Settings.SMTPServer, Settings.SMTPPort, SecureSocketOptions.None).ConfigureAwait(false);
                client.SendAsync(emailMessage).ConfigureAwait(false);
                client.DisconnectAsync(true).ConfigureAwait(false);
            }

            emailMessage = null;
            
            return testEmailToken;
        }

        private double? FetchTestMail(string testEmailToken, ILogger logger)
        {
            var emailDeliveryTiming = (double?)null;

            using (var client = new ImapClient())
            {
                client.Connect(
                    Settings.IMAPServer,
                    Settings.IMAPPort,
                    Settings.IMAPUseSSL);

                client.AuthenticationMechanisms.Remove("XOAUTH2");
                client.Authenticate(
                    Settings.IMAPUsername,
                    Settings.IMAPPassword);

                // The Inbox folder is always available on all IMAP servers...
                var inbox = client.Inbox;
                inbox.Open(FolderAccess.ReadWrite);

                logger.LogDebug($"Connected to IMAP Mailbox - {inbox.Count} messages in inbox");

                for (int i = 0; i < inbox.Count; i++)
                {
                    var message = inbox.GetMessage(i);

                    logger.LogDebug($"Processing message {i}");

                    if (message.Subject.Contains($"{EMAIL_TOKEN_PREFIX}{testEmailToken}") || message.TextBody.Contains($"{EMAIL_TOKEN_PREFIX}{testEmailToken}"))
                    {
                        logger.LogDebug("Target system checker message found");
                        logger.LogDebug("Subject: {0}", message.Subject);
                        logger.LogDebug("TextBody: {0}", message.TextBody);

                        //var results = inbox.Search(SearchOptions.All,SearchQuery.BodyContains($"Subject: {EMAIL_TOKEN_PREFIX}{testEmailToken}"))

                        var receivedHeader = message.Headers["Received"];

                        if (receivedHeader.Contains(";"))
                        {
                            var receivedDateString = receivedHeader.Split(';')[1];
                            DateTime receivedDate;

                            if (DateTime.TryParse(receivedDateString, out receivedDate))
                            {
                                emailDeliveryTiming = receivedDate.Subtract(message.Date.LocalDateTime).TotalSeconds;

                                logger.LogDebug(
                                    $"Sent {message.Date.LocalDateTime} Received {receivedDate} Elapsed Seconds {emailDeliveryTiming}");
                            }
                            else
                            {
                                logger.LogDebug("RECEIVED header malformed - datetime cannot be parsed");
                                emailDeliveryTiming = -1;
                            }
                        }
                        else
                        {
                            logger.LogDebug("RECEIVED header malformed - missing ; so unable to find received datetime for comparison");
                            emailDeliveryTiming = -1;
                        }
                        
                        inbox.AddFlags(new int[] {i}, MessageFlags.Deleted | MessageFlags.Seen, false);
                    }
                    else if 
                        (
                        (message.Subject.Contains(EMAIL_SUBJECT_PREFIX) || message.TextBody.Contains(EMAIL_SUBJECT_PREFIX) ) &&
                        (message.Subject.Contains(EMAIL_TOKEN_PREFIX) || message.TextBody.Contains(EMAIL_TOKEN_PREFIX))
                        ) 
                        // if it's from the system checker but another test email, probably an old one which took too long, then clean it up
                    {
                        logger.LogDebug("Old system checker message - marking for delete");
                        inbox.AddFlags(new int[] { i }, MessageFlags.Deleted | MessageFlags.Seen, false);
                    }
                }

                logger.LogDebug("Expunging deleted items from mailbox");
                client.Inbox.Expunge();

                client.Disconnect(true);
                logger.LogDebug("IMAP Logout");
            }

            return emailDeliveryTiming;
        }
    }
}
