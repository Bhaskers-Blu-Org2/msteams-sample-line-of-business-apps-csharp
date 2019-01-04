﻿using CrossVertical.Announcement.Helper;
using CrossVertical.Announcement.Helpers;
using CrossVertical.Announcement.Models;
using CrossVertical.Announcement.Repository;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Connector.Teams;
using Microsoft.Bot.Connector.Teams.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace CrossVertical.Announcement.Dialogs
{

    /// <summary>
    /// This Dialog enables the user to create, view and edit announcements.
    /// </summary>
    [Serializable]
    public class RootDialog : IDialog<object>
    {
        /// <summary>
        /// This is the name of the OAuth Connection Setting that is configured for this bot
        /// </summary>
        public async Task StartAsync(IDialogContext context)
        {
            context.Wait(MessageReceivedAsync);
        }

        private const string ProfileKey = "profile";

        /// <summary>
        /// Supports the commands recents, send, me, and signout against the Graph API
        /// </summary>
        private async Task MessageReceivedAsync(IDialogContext context, IAwaitable<object> result)
        {
            var activity = await result as Activity;

            string message = string.Empty;
            if (activity.Text != null)
                message = Microsoft.Bot.Connector.Teams.ActivityExtensions.GetTextWithoutMentions(activity).ToLowerInvariant();

            // Check if User or Team is registered.
            string profileKey = GetKey(activity, ProfileKey);
            User userDetails = null;
            var channelData = context.Activity.GetChannelData<TeamsChannelData>();
            if (context.ConversationData.ContainsKey(profileKey))
            {
                userDetails = context.ConversationData.GetValue<User>(profileKey);
            }
            else
            {
                userDetails = await CheckAndAddUserDetails(activity, channelData);
                context.ConversationData.SetValue<User>(profileKey, userDetails);
            }
            // Check and add tenant details
            if (activity.Attachments != null && activity.Attachments.Any(a => a.ContentType == FileDownloadInfo.ContentType))
            {
                var attachment = activity.Attachments.First();
                await HandleExcelAttachement(context, attachment, channelData);
            }
            else if (activity.Value != null)
            {
                await HandleActions(context, activity);
            }
            else
            {
                if (message.ToLowerInvariant().Contains("configure"))
                {
                    // Perform configuration.
                }
                else if (message.ToLowerInvariant().Contains("reset"))
                {
                    // Reset if needed
                }
                else
                {
                    var reply = activity.CreateReply();
                    if (channelData.Team != null)
                    {
                        if (message != Constants.ShowWelcomeScreen.ToLower())
                        {
                            reply.Text = "Announcements app is notification only in teams and channels. Please use the app in 1:1 chat to interact meaningfully.";
                            await context.PostAsync(reply);
                            return;
                        }
                    }

                    reply.Attachments.Add(AdaptiveCardDesigns.GetWelcomeScreen(channelData.Team != null));

                    await context.PostAsync(reply);
                }
            }
        }

        private static async Task HandleExcelAttachement(IDialogContext context, Attachment attachment, TeamsChannelData channelData)
        {
            if (attachment.ContentType == FileDownloadInfo.ContentType)
            {
                FileDownloadInfo downloadInfo = (attachment.Content as JObject).ToObject<FileDownloadInfo>();
                var filePath = System.Web.Hosting.HostingEnvironment.MapPath("~/Files/");
                if (!Directory.Exists(filePath))
                    Directory.CreateDirectory(filePath);

                filePath += attachment.Name + DateTime.Now.Millisecond; // just to avoid name collision with other users. 
                if (downloadInfo != null)
                {
                    using (WebClient myWebClient = new WebClient())
                    {
                        // Download the Web resource and save it into the current filesystem folder.
                        myWebClient.DownloadFile(downloadInfo.DownloadUrl, filePath);

                    }
                    if (File.Exists(filePath))
                    {
                        var groupDetails = ExcelHelper.GetAddTeamDetails(filePath);
                        if (groupDetails != null)
                        {
                            var tenantData = await CheckAndAddTenantDetails(channelData);
                            // Clean up earlier group data
                            foreach (var groupId in tenantData.Groups)
                            {
                                await Cache.Groups.DeleteItemAsync(groupId);
                            }
                            tenantData.Groups.Clear();

                            foreach (var groupDetail in groupDetails)
                            {
                                await Cache.Groups.AddOrUpdateItemAsync(groupDetail.Id, groupDetail);
                                tenantData.Groups.Add(groupDetail.Id);
                            }
                            await Cache.Tenants.AddOrUpdateItemAsync(tenantData.Id, tenantData);
                        }
                        else
                        {
                            await context.PostAsync($"Attachment received but unfortunately we are not able to read group details. Please make sure that all the colums are correct.");
                        }

                        await context.PostAsync($"Successfully updated Group details for this tenant.");
                        File.Delete(filePath);
                    }
                }
            }
        }

        internal async static Task<User> CheckAndAddUserDetails(Activity activity, TeamsChannelData channelData)
        {
            var userEmailId = await GetUserEmailId(activity);
            // User not present in cache
            var userDetails = await Cache.Users.GetItemAsync(userEmailId);
            if (userDetails == null)
            {
                userDetails = new User()
                {
                    BotConversationId = activity.From.Id,
                    EmailId = userEmailId,
                    Name = activity.From.Name
                };
                await Cache.Users.AddOrUpdateItemAsync(userDetails.EmailId, userDetails);

                Tenant tenantData = await CheckAndAddTenantDetails(channelData);
                if (!tenantData.Users.Contains(userDetails.EmailId))
                {
                    tenantData.Users.Add(userDetails.EmailId);
                    await Cache.Tenants.AddOrUpdateItemAsync(tenantData.Id, tenantData);
                }
            }

            return userDetails;
        }

        internal static async Task<Tenant> CheckAndAddTenantDetails(TeamsChannelData channelData)
        {
            // Tenant not present in cached check DB
            var tenantData = await Cache.Tenants.GetItemAsync(channelData.Tenant.Id);
            if (tenantData == null)
            {
                tenantData = new Tenant()
                {
                    Id = channelData.Tenant.Id,
                };
                await Cache.Tenants.AddOrUpdateItemAsync(tenantData.Id, tenantData);
            }

            return tenantData;
        }

        private static string GetKey(IActivity activity, string key)
        {
            return activity.From.Id + key;
        }

        private static async Task<string> GetUserEmailId(Activity activity)
        {
            // Fetch the members in the current conversation
            ConnectorClient connector = new ConnectorClient(new Uri(activity.ServiceUrl));
            var members = await connector.Conversations.GetConversationMembersAsync(activity.Conversation.Id);
            return members.Where(m => m.Id == activity.From.Id).First().AsTeamsChannelAccount().UserPrincipalName;
        }

        private async Task HandleActions(IDialogContext context, Activity activity)
        {
            string type = string.Empty;
            try
            {
                // For Task Module Edit
                var details = JsonConvert.DeserializeObject<TaskModule.BotFrameworkCardValue<ActionDetails>>(activity.Value.ToString());
                type = details.Data.ActionType;
            }
            catch (Exception)
            {
                var details = JsonConvert.DeserializeObject<ActionDetails>(activity.Value.ToString());
                type = details.ActionType;
            }

            var channelData = context.Activity.GetChannelData<TeamsChannelData>();
            switch (type)
            {
                case Constants.CreateOrEditAnnouncement:
                    // Save in DB & Send preview card
                    await CreateOrEditAnnouncement(context, activity, channelData);
                    break;
                case Constants.Configure:
                    // Allow user to configure the groups.
                    await SendConfigurationCard(context, activity, channelData);
                    break;
                case Constants.SendAnnouncement:
                    await SendAnnouncement(context, activity, channelData);
                    break;
                case Constants.Acknowledge:
                    await SaveAcknowledgement(context, activity, channelData);
                    break;
                case Constants.ShowAllDrafts:
                    await ShowAllDrafts(context, activity, channelData);
                    break;
                case Constants.ShowAnnouncement:
                    await ShowAnnouncementDraft(context, activity, channelData);
                    break;
                default:
                    break;
            }
        }

        private async Task ShowAnnouncementDraft(IDialogContext context, Activity activity, TeamsChannelData channelData)
        {
            var details = JsonConvert.DeserializeObject<AnnouncementActionDetails>(activity.Value.ToString());
            if (details != null)
            {
                var campaign = await Cache.Announcements.GetItemAsync(details.Id);
                if (campaign == null)
                    return;
                await SendPreviewCard(context, activity, campaign, false);
            }
        }

        private async Task ShowAllDrafts(IDialogContext context, Activity activity, TeamsChannelData channelData)
        {

            var tenatInfo = await Cache.Tenants.GetItemAsync(channelData.Tenant.Id);
            var myTenantAnnouncements = new List<Campaign>();

            var listCard = new ListCard();
            listCard.content = new Content();
            listCard.content.title = "Here are all the announcement drafts:"; ;
            var list = new List<Item>();
            foreach (var announcementId in tenatInfo.Announcements)
            {
                var announcement = await Cache.Announcements.GetItemAsync(announcementId);
                if (announcement != null && announcement.Status == Status.Draft)
                {
                    var item = new Item
                    {
                        icon = announcement.Author.ProfilePhoto,
                        type = "resultItem",
                        id = announcement.Id,
                        title = announcement.Title,
                        subtitle = "Author: " + announcement.Author?.Name
                             + $" | Created Date: {announcement.CreatedTime.ToShortDateString()}",
                        tap = new Tap()
                        {
                            type = ActionTypes.MessageBack,
                            title = "Id",
                            value = JsonConvert.SerializeObject(new AnnouncementActionDetails()
                            { Id = announcement.Id, ActionType = Constants.ShowAnnouncement }) //  "Show Announcement " + announcement.Title + " (" + announcement.Id + ")"
                        }
                    };
                    list.Add(item);
                }
            }

            if (list.Count > 0)
            {
                listCard.content.items = list.ToArray();
                Attachment attachment = new Attachment();
                attachment.ContentType = listCard.contentType;
                attachment.Content = listCard.content;
                var reply = activity.CreateReply();
                reply.Attachments.Add(attachment);
                await context.PostAsync(reply);
            }
            else
                await context.PostAsync("Thre are no drafts. Please go ahead and create new announcement.");
        }

        private async Task SaveAcknowledgement(IDialogContext context, Activity activity, TeamsChannelData channelData)
        {
            // Get all the details for announcement.
            var details = JsonConvert.DeserializeObject<AnnouncementAcknowledgeActionDetails>(activity.Value.ToString());
            if (details == null || details.Id == null)
            {
                details = JsonConvert.DeserializeObject<TaskModule.BotFrameworkCardValue<AnnouncementAcknowledgeActionDetails>>
                    (activity.Value.ToString()).Data;
            }
            // Add announcemnet in DB.
            var campaign = await Cache.Announcements.GetItemAsync(details.Id);
            if (campaign == null)
            {
                await context.PostAsync("This campaing has been removed. Please contact campaign owner.");
                return;
            }
            var group = campaign.Recipients.Groups.FirstOrDefault(g => g.GroupId == details.GroupId);
            if (campaign.Status != Status.Sent)
            {
                // await context.PostAsync("This will send acknowlegement when user clicks on it.");
                return;
            }
            if (group != null)
            {
                var user = group.Users.FirstOrDefault(u => u.Id == details.UserId);
                if (user != null)
                {
                    if (!user.IsAcknoledged)
                    {
                        user.IsAcknoledged = true;
                        await Cache.Announcements.AddOrUpdateItemAsync(campaign.Id, campaign);
                        await context.PostAsync("Your response has been recorded.");
                    }
                    else
                        await context.PostAsync("Your response is already recorded.");
                }
            }
        }

        private async Task SendAnnouncement(IDialogContext context, Activity activity, TeamsChannelData channelData)
        {
            // Get all the details for announcement.
            var details = JsonConvert.DeserializeObject<AnnouncementActionDetails>(activity.Value.ToString());
            // Add announcemnet in DB.
            var campaign = await Cache.Announcements.GetItemAsync(details.Id);
            if (campaign == null)
            {
                await context.PostAsync("Unable to find this announcement. Please create new announcement.");
                return;
            }
            if (campaign.Status == Status.Sent)
            {
                await context.PostAsync("This announcement is already sent and can not be resent. Please create new announcement.");
                return;
            }
            else
                await context.PostAsync("Please wait while we send this announcement to all recipients.");

            if (campaign.Recipients.Channels.Count == 0 && campaign.Recipients.Groups.Count == 0)
            {
                await context.PostAsync("No recipients. Please select at least one recipient.");
                return;
            }

            var card = campaign.GetPreviewCard().ToAttachment();

            int successCount = 0;
            int failureCount = 0;
            int duplicateUsers = 0;
            List<string> notifiedUsers = new List<string>();
            foreach (var group in campaign.Recipients.Groups)
            {
                foreach (var recipient in group.Users)
                {
                    var user = await Cache.Users.GetItemAsync(recipient.Id);
                    if (user == null)
                    {
                        recipient.FailureMessage = "App not installed";
                        failureCount++;
                        await context.PostAsync("App not installed " + recipient.Id);
                    }
                    else
                    {
                        if (notifiedUsers.Contains(recipient.Id))
                        {
                            recipient.FailureMessage = "Duplicated. Message already sent.";
                            duplicateUsers++;
                            continue;
                        }

                        var campaignCard = AdaptiveCardDesigns.GetCardWithAcknowledgementDetails(card, campaign.Id, user.EmailId, group.GroupId);
                        var response = await SendNotification(activity.ServiceUrl, channelData.Tenant.Id, user.BotConversationId, null, card);
                        if (response.IsSuccessful)
                        {
                            recipient.MessageId = response.MessageId;
                            successCount++;
                            notifiedUsers.Add(recipient.Id);
                        }
                        else
                        {
                            recipient.FailureMessage = response.FailureMessage;
                            failureCount++;
                            await context.PostAsync($"User: {user.Name}. Error: " + recipient.FailureMessage);
                        }
                    }
                }
            }

            foreach (var recipient in campaign.Recipients.Channels)
            {
                var team = await Cache.Teams.GetItemAsync(recipient.TeamId);
                if (team == null)
                {
                    recipient.Channel.FailureMessage = "App not installed";
                    failureCount++;
                    await context.PostAsync("App not installed " + recipient.TeamId);
                }
                else
                {
                    var campaignCard = AdaptiveCardDesigns.GetCardWithoutAcknowledgementAction(card);

                    if (notifiedUsers.Contains(recipient.Channel.Id))
                    {
                        recipient.Channel.FailureMessage = "Duplicated. Message already sent.";
                        duplicateUsers++;
                        continue;
                    }

                    var response = await SendChannelNotification(activity.From, activity.ServiceUrl, recipient.Channel.Id, null, card);
                    if (response.IsSuccessful)
                    {
                        recipient.Channel.MessageId = response.MessageId;
                        successCount++;
                        notifiedUsers.Add(recipient.Channel.Id);
                    }
                    else
                    {
                        recipient.Channel.FailureMessage = response.FailureMessage;
                        failureCount++;
                        await context.PostAsync($"Team: {team.Name}. Error: " + recipient.Channel.FailureMessage);
                    }
                }
            }

            await context.PostAsync($"Process completed. Successful: {successCount}. Failure: {failureCount}. Duplicate: {duplicateUsers}");
            campaign.Status = Status.Sent;
            await Cache.Announcements.AddOrUpdateItemAsync(campaign.Id, campaign);

            var oldAnnouncementDetails = context.ConversationData.GetValueOrDefault<PreviewCardMessageDetails>(campaign.Id);
            if (oldAnnouncementDetails != null)
            {
                ConnectorClient connectorClient = new ConnectorClient(new Uri(activity.ServiceUrl));

                // Update card.
                var updateCard = campaign.GetPreviewCard().ToAttachment();

                var updateMessage = context.MakeMessage();
                updateMessage.Attachments.Add(AdaptiveCardDesigns.GetCardToUpdatePreviewCard(updateCard));
                await connectorClient.Conversations.UpdateActivityAsync(activity.Conversation.Id, oldAnnouncementDetails.MessageCardId, (Activity)updateMessage);

                // Update action card.
                var updateAnnouncement = AdaptiveCardDesigns.GetUpdateMessageCard("We have send this announcement successfully. Please create new announcement to send again.");
                updateMessage = context.MakeMessage();
                updateMessage.Attachments.Add(updateAnnouncement);
                await connectorClient.Conversations.UpdateActivityAsync(activity.Conversation.Id, oldAnnouncementDetails.MessageActionId, (Activity)updateMessage);
                context.ConversationData.RemoveValue(campaign.Id);
            }
        }

        private async Task CreateOrEditAnnouncement(IDialogContext context, Activity activity, TeamsChannelData channelData)
        {
            // Get all the details for announcement.
            var details = JsonConvert.DeserializeObject<TaskModule.BotFrameworkCardValue<CreateNewAnnouncementData>>(activity.Value.ToString());

            // Add announcemnet in DB.
            var campaign = await AddAnnouncecmentInDB(activity, details.Data, channelData.Tenant.Id);
            await SendPreviewCard(context, activity, campaign, true);

        }

        private static async Task SendPreviewCard(IDialogContext context, Activity activity, Campaign campaign, bool isEditPreview)
        {
            ConnectorClient connector = new ConnectorClient(new Uri(activity.ServiceUrl));
            var reply = activity.CreateReply();
            reply.Attachments.Add(campaign.GetPreviewCard().ToAttachment());

            // await context.PostAsync(reply);

            PreviewCardMessageDetails previewMessageDetails = null;
            if (isEditPreview)
                previewMessageDetails = context.ConversationData.GetValueOrDefault<PreviewCardMessageDetails>(campaign.Id);

            if (previewMessageDetails == null)
            {
                var messageResouce = await connector.Conversations.SendToConversationAsync(reply);
                previewMessageDetails = new PreviewCardMessageDetails()
                {
                    MessageCardId = messageResouce.Id
                };

                // Send action buttons.
                reply = activity.CreateReply();
                reply.Attachments.Add(AdaptiveCardDesigns.GetConfirmationCard(campaign.Id));
                messageResouce = await connector.Conversations.SendToConversationAsync(reply);
                previewMessageDetails.MessageActionId = messageResouce.Id;
                context.ConversationData.SetValue(campaign.Id, previewMessageDetails);
            }
            else
            {
                await connector.Conversations.UpdateActivityAsync(activity.Conversation.Id, previewMessageDetails.MessageCardId, reply);
            }
        }

        private async Task SendConfigurationCard(IDialogContext context, Activity activity, TeamsChannelData channelData)
        {
            var configurationCard = new ThumbnailCard
            {
                Text = @"Please go ahead and upload the excel file with Group details in following format:  
                        <ol>
                        <li><strong>Group Name</strong>: String eg: <pre>All Employees</pre></li>
                        <li><strong>Members</strong>  : Comma separated user emails eg: <pre>user1@org.com, user2@org.com</pre></li></ol>
                        </br> <strong>Note: Please keep first row header as described above. You can provide details for multiple teams row by row. Members/Channels columns can be empty.</strong>",
            };
            var tenant = await Cache.Tenants.GetItemAsync(channelData.Tenant.Id);
            if (!tenant.IsAdminConsented)
            {
                configurationCard.Text = "Please grant permission to the application first. Once that is done you can upload excel file with group details.";
                // Show button with Open URl.
                var loginUrl = GetAdminConsentUrl(channelData.Tenant.Id, ApplicationSettings.AppId);
                configurationCard.Buttons = new List<CardAction>();
                configurationCard.Buttons.Add(new CardAction()
                {
                    Title = "Grant Admin Permission",
                    Value = loginUrl,
                    Type = ActionTypes.OpenUrl
                });
            }

            var reply = activity.CreateReply();
            reply.Attachments.Add(configurationCard.ToAttachment());
            await context.PostAsync(reply);
        }

        private static string GetAdminConsentUrl(string tenant, string appId)
        {
            return $"https://login.microsoftonline.com/{tenant}/adminconsent?client_id={appId}&state=12345&redirect_uri={ System.Web.HttpUtility.UrlEncode(ApplicationSettings.BaseUrl + "/adminconsent")}";
        }

        private async Task<Campaign> AddAnnouncecmentInDB(Activity activity, CreateNewAnnouncementData data, string tenantId)
        {
            Campaign announcement = new Campaign()
            {
                IsAcknowledgementRequested = bool.Parse(data.Acknowledge),
                IsContactAllowed = bool.Parse(data.AllowContactIns),
                ShowAllDetailsButton = true,
                Title = data.Title,
                SubTitle = data.SubTitle,
                CreatedTime = DateTime.Now,
                Author = new Author()
                {
                    EmailId = data.AuthorAlias
                },
                Preview = data.Preview,
                Body = data.Body,
                ImageUrl = data.Image,
                Sensitivity = (MessageSensitivity)Enum.Parse(typeof(MessageSensitivity), data.MessageType),
                OwnerId = await GetUserEmailId(activity)
            };

            announcement.Id = string.IsNullOrEmpty(data.Id) ? Guid.NewGuid().ToString() : data.Id; // Assing the Existing Announcement Id
            announcement.TenantId = tenantId;

            var recipients = new RecipientInfo();
            if (data.Channels != null)
            {
                var channels = data.Channels.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var channelInfo in channels)
                {
                    var info = channelInfo.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                    recipients.Channels.Add(new ChannelRecipient()
                    {
                        TeamId = info[0],
                        Channel = new RecipientDetails()
                        {
                            Id = info[1],
                        }
                    });
                }
            }

            if (data.Groups != null)
            {
                var groups = data.Groups.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var groupId in groups)
                {
                    var groupDetailsFromDB = await Cache.Groups.GetItemAsync(groupId);
                    var recipientGroup = new GroupRecipient()
                    {
                        GroupId = groupId
                    };
                    foreach (var userEmailId in groupDetailsFromDB.Users)
                    {
                        recipientGroup.Users.Add(new RecipientDetails()
                        {
                            Id = userEmailId,
                        });
                    }
                    recipients.Groups.Add(recipientGroup);
                }
            }
            announcement.Recipients = recipients;
            announcement.Status = Status.Draft;

            var tenantDetails = await Cache.Tenants.GetItemAsync(tenantId);
            // Fetch author Email ID
            if (tenantDetails.IsAdminConsented)
            {
                var token = await GraphHelper.GetAccessToken(tenantId, ApplicationSettings.AppId, ApplicationSettings.AppSecret);
                GraphHelper helper = new GraphHelper(token);
                var userDetails = await helper.GetUser(announcement.Author.EmailId);
                if (userDetails != null)
                {
                    announcement.Author.Name = userDetails.DisplayName;
                    announcement.Author.Role = userDetails.JobTitle ?? userDetails.UserPrincipalName;
                    announcement.Author.ProfilePhoto = await helper.GetProfilePhoto(userDetails.Id);
                }
            }

            if (string.IsNullOrEmpty(data.Id))
            {
                await Cache.Announcements.AddOrUpdateItemAsync(announcement.Id, announcement);// Push to DB
                tenantDetails.Announcements.Add(announcement.Id);
                await Cache.Tenants.AddOrUpdateItemAsync(tenantDetails.Id, tenantDetails); // Update tenat catalog
            }
            else
                await Cache.Announcements.AddOrUpdateItemAsync(data.Id, announcement);// Push updated data DB

            return announcement;
        }

        public static async Task<NotificationSendStatus> SendNotification(string serviceUrl, string tenantId, string userId, string messageText, Attachment attachment)
        {
            var connectorClient = new ConnectorClient(new Uri(serviceUrl));

            var parameters = new ConversationParameters
            {
                Members = new ChannelAccount[] { new ChannelAccount(userId) },
                ChannelData = new TeamsChannelData
                {
                    Tenant = new TenantInfo(tenantId),
                    Notification = new NotificationInfo() { Alert = true }
                }
            };

            try
            {
                var conversationResource = await connectorClient.Conversations.CreateConversationAsync(parameters);
                var replyMessage = Activity.CreateMessageActivity();
                replyMessage.Conversation = new ConversationAccount(id: conversationResource.Id.ToString());
                replyMessage.ChannelData = new TeamsChannelData() { Notification = new NotificationInfo(true) };
                replyMessage.Text = messageText;
                if (attachment != null)
                    replyMessage.Attachments.Add(attachment);

                var resourceResponse = await connectorClient.Conversations.SendToConversationAsync(conversationResource.Id, (Activity)replyMessage);
                return new NotificationSendStatus() { MessageId = resourceResponse.Id, IsSuccessful = true };

            }
            catch (Exception ex)
            {
                // Handle the error.
                ErrorLogService.LogError(ex);
                return new NotificationSendStatus() { IsSuccessful = false, FailureMessage = ex.Message };
            }
        }

        private static async Task<NotificationSendStatus> SendChannelNotification(ChannelAccount botAccount, string serviceUrl, string channelId, string messageText, Attachment attachment)
        {
            try
            {
                var replyMessage = Activity.CreateMessageActivity();
                replyMessage.Text = messageText;

                if (attachment != null)
                    replyMessage.Attachments.Add(attachment);

                var connectorClient = new ConnectorClient(new Uri(serviceUrl));

                var parameters = new ConversationParameters
                {
                    Bot = botAccount,
                    ChannelData = new TeamsChannelData
                    {
                        Channel = new ChannelInfo(channelId),
                        Notification = new NotificationInfo() { Alert = true }
                    },
                    IsGroup = true,
                    Activity = (Activity)replyMessage
                };

                var conversationResource = await connectorClient.Conversations.CreateConversationAsync(parameters);
                return new NotificationSendStatus() { MessageId = conversationResource.Id, IsSuccessful = true };
            }
            catch (Exception ex)
            {
                ErrorLogService.LogError(ex);
                return new NotificationSendStatus() { IsSuccessful = false, FailureMessage = ex.Message };
            }
        }

    }
}