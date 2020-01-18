using DiscordAPI.Models;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using JetBrains.Annotations;
using Quarrel.Messages.Gateway;
using Quarrel.Messages.Navigation;
using Quarrel.Messages.Posts.Requests;
using Quarrel.Models.Bindables;
using Quarrel.Navigation;
using Quarrel.Services.Cache;
using Quarrel.Services.Gateway;
using Quarrel.Services.Guild;
using Quarrel.Services.Rest;
using Quarrel.Services.Settings;
using Quarrel.Services.Users;
using Quarrel.ViewModels.Helpers;
using Quarrel.ViewModels.Messages.Gateway;
using Quarrel.ViewModels.Models.Bindables;
using Quarrel.ViewModels.Models.Interfaces;
using Quarrel.ViewModels.Services;
using Quarrel.ViewModels.Services.DispatcherHelper;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Quarrel.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        #region Constructors

        /// <summary>
        /// Creates default MainViewModel with all Messenger events registered
        /// </summary>
        /// <remarks>Takes all service parameters from ViewModel Locator</remarks>
        public MainViewModel(ICacheService cacheService, ISettingsService settingsService,
            IDiscordService discordService, ICurrentUsersService currentUsersService, IGatewayService gatewayService,
            IGuildsService guildsService, ISubFrameNavigationService subFrameNavigationService,
            IDispatcherHelper dispatcherHelper)
        {
            CacheService = cacheService;
            SettingsService = settingsService;
            DiscordService = discordService;
            CurrentUsersService = currentUsersService;
            GatewayService = gatewayService;
            GuildsService = guildsService;
            SubFrameNavigationService = subFrameNavigationService;
            DispatcherHelper = dispatcherHelper;

            RegisterMessages();
        }

        #endregion

        #region Events

        /// <summary>
        /// Scrolls message list to BindableMessage
        /// </summary>
        public event EventHandler<BindableMessage> ScrollTo;

        #endregion

        #region Commands

        #region GuildSubscriptions

        private RelayCommand<(double, double)> updateGuildSubscriptionsCommand;
        public RelayCommand<(double, double)> UpdateGuildSubscriptionsCommand =>
            updateGuildSubscriptionsCommand ??= new RelayCommand<(double, double)>((values) =>
            {
                if (guildId == "DM")
                    return;


                double top = BindableMembersNew.Count * values.Item1;
                double bottom = BindableMembersNew.Count * values.Item2;

                int min = (int)Math.Floor(top / 100) * 100;
                var guildSubscription = new Dictionary<string, IEnumerable<int[]>>
                {
                    {
                        Channel.Model.Id,
                        new List<int[]>
                        {
                            new[] { 0, 99 }
                        }
                    }
                };
                if (top - min < 20)
                {
                    if (min > 199)
                        ((List<int[]>)guildSubscription[Channel.Model.Id]).Add(new[] { min - 100, min - 1 });
                    if (min > 99)
                        ((List<int[]>)guildSubscription[Channel.Model.Id]).Add(new[] { min, min + 99 });
                }
                else if (bottom - min > 80)
                {
                    ((List<int[]>)guildSubscription[Channel.Model.Id]).Add(new[] { min, min + 99 });
                    ((List<int[]>)guildSubscription[Channel.Model.Id]).Add(new[] { min + 100, min + 199 });
                }
                else
                {
                    if (min > 99)
                        ((List<int[]>)guildSubscription[Channel.Model.Id]).Add(new[] { min, min + 99 });
                }

                bool hasChanged = false;

                // Check if anything has changed
                if (lastGuildSubscription != null && lastGuildSubscription.Count == guildSubscription.Count)
                {
                    foreach (var channel in lastGuildSubscription)
                    {
                        if (guildSubscription.ContainsKey(channel.Key))
                        {
                            if (channel.Value.Count() == guildSubscription[channel.Key].Count())
                            {

                                var enumerator = guildSubscription[channel.Key].GetEnumerator();
                                foreach (var range in channel.Value)
                                {
                                    enumerator.MoveNext();
                                    if (!(range[0] == enumerator.Current[0] && range[1] == enumerator.Current[1]))
                                    {
                                        hasChanged = true;
                                    }

                                }
                            }
                            else
                            {
                                hasChanged = true;
                            }
                        }
                        else
                        {
                            hasChanged = true;
                        }
                    }
                }
                else
                {
                    hasChanged = true;
                }

                if (hasChanged)
                {
                    Messenger.Default.Send(new GatewayUpdateGuildSubscriptionsMessage(guildId, guildSubscription));
                    lastGuildSubscription = guildSubscription;
                }

            });

        #endregion

        #region Navigation

        /// <summary>
        /// Sends Messenger Request to change Guild
        /// </summary>
        private RelayCommand<BindableGuild> navigateGuildCommand;
        public RelayCommand<BindableGuild> NavigateGuildCommand => navigateGuildCommand ??=
            new RelayCommand<BindableGuild>((guild) => { MessengerInstance.Send(new GuildNavigateMessage(guild)); });

        /// <summary>
        /// Sends Messenger Request to change Channel
        /// </summary>
        private RelayCommand<BindableChannel> navigateChannelCommand;
        public RelayCommand<BindableChannel> NavigateChannelCommand => navigateChannelCommand ??=
            new RelayCommand<BindableChannel>(async (channel) =>
            {
                Channel = channel;
                if (channel.IsCategory)
                {
                    bool newState = !channel.Collapsed;
                    for (int i = Guild.Channels.IndexOf(channel);
                        i < Guild.Channels.Count
                        && Guild.Channels[i] != null
                        && Guild.Channels[i].ParentId == channel.Model.Id;
                        i++)
                        Guild.Channels[i].Collapsed = newState;
                }
                else if (channel.IsVoiceChannel)
                {
                    if (channel.Model is GuildChannel gChannel)
                        await DiscordService.Gateway.Gateway.VoiceStatusUpdate(Guild.Model.Id, gChannel.Id, false,
                            false);
                }
                else if (channel.Permissions.ReadMessages)
                {
                    MessengerInstance.Send(new ChannelNavigateMessage(channel, Guild));
                }
            });

        /// <summary>
        /// Sets null channel and clear messages to show Friends Panel
        /// </summary>
        private RelayCommand navigateToFriends;
        public RelayCommand NavigateToFriends => navigateToFriends = new RelayCommand(() =>
        {
            if (Channel != null)
            {
                Channel.Selected = false;
                Channel = null;
            }

            BindableMessages.Clear();
        });

        #endregion

        #region Messages

        /// <summary>
        /// Sends API message to indicate typing state
        /// </summary>
        private RelayCommand tiggerTyping;
        public RelayCommand TriggerTyping => tiggerTyping ??= new RelayCommand(() =>
        {
            DiscordService.ChannelService.TriggerTypingIndicator(Channel.Model.Id);
        });

        /// <summary>
        /// Handles enter override on MessageBox to add new line
        /// </summary>
        private RelayCommand newLineCommand;
        public RelayCommand NewLineCommand =>
            newLineCommand ??= new RelayCommand(() =>
            {
                string text = MessageText;
                int selectionstart = SelectionStart;

                if (SelectionLength > 0)
                    // Remove selected text first
                    text = text.Remove(selectionstart, SelectionLength);

                text = text.Insert(selectionstart, " \n");
                MessageText = text;
                SelectionStart = selectionstart + 2;
            });

        /// <summary>
        /// Handles enter override on MessageBox to send message
        /// </summary>
        private RelayCommand sendMessageCommand;
        public RelayCommand SendMessageCommand => sendMessageCommand ??= new RelayCommand(async () =>
        {
            string text = MessageText;

            // Parses out Mentions
            List<string> mentions = FindMentions(text);
            foreach (string mention in mentions)
                if (mention[0] == '@')
                {
                    // Replaces username descriminator format with Id format
                    int discIndex = mention.IndexOf('#');
                    string username = mention.Substring(1, discIndex - 1);
                    string disc = mention.Substring(1 + discIndex);
                    User user;

                    user = CurrentUsersService.Users.Values.FirstOrDefault(x =>
                        x.Model.User.Username == username && x.Model.User.Discriminator == disc).Model.User;

                    if (user != null)
                        text = text.Replace("@" + user.Username + "#" + user.Discriminator, "<@!" + user.Id + ">");
                }
                else if (mention[0] == '#')
                {
                    // Replaces channel name format with Id format
                    if (!Guild.IsDM)
                    {
                        Channel channel = Guild.Channels
                            .FirstOrDefault(x => x.Model.Type != 4 && x.Model.Name == mention.Substring(1)).Model;
                        text = text.Replace("#" + channel.Name, "<#" + channel.Id + ">");
                    }
                }

            await DiscordService.ChannelService.CreateMessage(Channel.Model.Id,
                new DiscordAPI.API.Channel.Models.MessageUpsert() { Content = text });
            DispatcherHelper.CheckBeginInvokeOnUi(() => { MessageText = ""; });
        });

        /// <summary>
        /// Override up arrow to edit last sent message in chat
        /// </summary>
        private RelayCommand editLastMessageCommand;
        public RelayCommand EditLastMessageCommand => editLastMessageCommand ??= new RelayCommand(() =>
        {
            // Only overrides if there's no draft
            if (string.IsNullOrEmpty(MessageText))
            {
                var userLastMessage = BindableMessages.LastOrDefault(x => x.Model.Id != "Ad" && x.Model.User.Id == CurrentUsersService.CurrentUser.Model.Id);
                if (userLastMessage != null)
                {
                    userLastMessage.IsEditing = true;
                    ScrollTo?.Invoke(this, userLastMessage);
                }
            }
        });

        /// <summary>
        /// Sends API request to delete a message
        /// </summary>
        private RelayCommand<BindableMessage> deleteMessageCommand;
        public RelayCommand<BindableMessage> DeleteMessageCommand => deleteMessageCommand ??=
            new RelayCommand<BindableMessage>(async (message) =>
            {
                await DiscordService.ChannelService.DeleteMessage(message.Model.ChannelId, message.Model.Id);
            });

        /// <summary>
        /// Sends API request to pin a message
        /// </summary>
        private RelayCommand<BindableMessage> pinMessageCommand;
        public RelayCommand<BindableMessage> PinMessageCommand => pinMessageCommand ??=
            new RelayCommand<BindableMessage>(async (message) =>
            {
                await DiscordService.ChannelService.AddPinnedChannelMessage(message.Model.ChannelId,
                    message.Model.Id);
            });

        /// <summary>
        /// Sends API request to unpin a message
        /// </summary>
        private RelayCommand<BindableMessage> unPinMessageCommand;
        public RelayCommand<BindableMessage> UnPinMessageCommand => unPinMessageCommand ??=
            new RelayCommand<BindableMessage>(async (message) =>
            {
                await DiscordService.ChannelService.DeletePinnedChannelMessage(message.Model.ChannelId,
                    message.Model.Id);
            });

        #endregion

        #region Voice

        /// <summary>
        /// Set VoiceStatus to null
        /// </summary>
        private RelayCommand disconnectVoiceCommand;
        public RelayCommand DisconnectVoiceCommand => disconnectVoiceCommand ??= new RelayCommand(async () =>
        {
            await GatewayService.Gateway.VoiceStatusUpdate(null, null, false, false);
        });

        #endregion

        #endregion

        #region Methods

        /// <summary>
        /// Register for MVVM Messenger messages in MainViewModel
        /// </summary>
        private void RegisterMessages()
        {
            #region Gateway 

            #region Initialize

            // Failed to login in due to invalid token
            // Deletes token and asks user to sign in again
            MessengerInstance.Register<GatewayInvalidSessionMessage>(this, async _ =>
            {
                await CacheService.Persistent.Roaming.DeleteValueAsync(Constants.Cache.Keys.AccessToken);
                Login();
            });

            // Ready Message recieved. Setsup Friend Collections and navigates to DM guild
            MessengerInstance.Register<GatewayReadyMessage>(this, _ =>
            {
                DispatcherHelper.CheckBeginInvokeOnUi(() =>
                {
                    MessengerInstance.Send(new GuildNavigateMessage(GuildsService.Guilds["DM"]));

                    // Show guilds
                    BindableGuilds.AddRange(GuildsService.Guilds.Values.OrderBy(x => x.Position));
                    BindableCurrentFriends.AddRange(CurrentUsersService.Friends.Values.Where(x => x.IsFriend));
                    BindablePendingFriends.AddRange(
                        CurrentUsersService.Friends.Values.Where(x => x.IsIncoming || x.IsOutgoing));
                    BindableBlockedUsers.AddRange(CurrentUsersService.Friends.Values.Where(x => x.IsBlocked));
                });
            });

            #endregion

            #region Messages

            // Handles incoming messages
            MessengerInstance.Register<GatewayMessageRecievedMessage>(this, m =>
            {
                // Check if channel exists
                if (GuildsService.CurrentChannels.TryGetValue(m.Message.ChannelId, out BindableChannel channel))
                {
                    channel.UpdateLMID(m.Message.Id);

                    // Updates Mention count
                    if (channel.IsDirectChannel || channel.IsGroupChannel ||
                        m.Message.Mentions.Any(x => x.Id == CurrentUsersService.CurrentUser.Model.Id) ||
                        m.Message.MentionEveryone)
                        DispatcherHelper.CheckBeginInvokeOnUi(() =>
                        {
                            channel.ReadState.MentionCount++;
                            if (channel.IsDirectChannel || channel.IsGroupChannel)
                            {
                                int oldIndex = GuildsService.Guilds["DM"].Channels.IndexOf(channel);
                                if(oldIndex >= 0)
                                    GuildsService.Guilds["DM"].Channels.Move(oldIndex, 0);
                            }
                        });

                    // Removes typer from Channel if responsible for sending this message
                    if (Channel != null && Channel.Model.Id == channel.Model.Id)
                        DispatcherHelper.CheckBeginInvokeOnUi(() =>
                        {
                            channel.Typers.TryRemove(m.Message.User.Id, out _);
                            BindableMessage lastMessage = BindableMessages.LastOrDefault();
                            BindableMessages.Add(new BindableMessage(m.Message, channel.Guild.Model.Id ?? "DM",
                                BindableMessages.LastOrDefault().Model.User != null && BindableMessages.LastOrDefault().Model.User.Id == m.Message.User.Id));
                        });
                }
            });

            // Handles message deletion
            MessengerInstance.Register<GatewayMessageDeletedMessage>(this, m =>
            {
                if (Channel != null && Channel.Model.Id == m.ChannelId)
                    DispatcherHelper.CheckBeginInvokeOnUi(() =>
                    {
                        BindableMessage msg = BindableMessages.LastOrDefault(x => x.Model.Id == m.MessageId);
                        if (msg != null) BindableMessages.Remove(msg);
                    });
            });

            // Handles message updated
            MessengerInstance.Register<GatewayMessageUpdatedMessage>(this, m =>
            {
                if (Channel != null && Channel.Model.Id == m.Message.ChannelId)
                    DispatcherHelper.CheckBeginInvokeOnUi(() =>
                    {
                        BindableMessage msg = BindableMessages.LastOrDefault(x => x.Model.Id != "Ad");
                        msg?.Update(m.Message);
                    });
            });

            #endregion

            #region Members
            
            // Handles VoiceState change for current user
            MessengerInstance.Register<GatewayVoiceStateUpdateMessage>(this, m =>
            {
                if (m.VoiceState.UserId == DiscordService.CurrentUser.Id)
                    DispatcherHelper.CheckBeginInvokeOnUi(() => VoiceState = m.VoiceState);
            });

            MessengerInstance.Register<GatewayGuildMemberListUpdatedMessage>(this, m =>
            {
                if (m.GuildMemberListUpdated.GuildId == guildId)
                    DispatcherHelper.CheckBeginInvokeOnUi(() =>
                    {
                        if (m.GuildMemberListUpdated.Id != listId &&
                            m.GuildMemberListUpdated.Operators.All(x => x.Op != "SYNC")) return;
                        if (m.GuildMemberListUpdated.Groups != null)
                        {
                            BindableMemeberGroups.Clear();
                            int totalMemberCount = 0;

                            foreach (Group group in m.GuildMemberListUpdated.Groups)
                            {
                                totalMemberCount += @group.Count + 1;
                                BindableMemeberGroups.Add(new BindableGuildMemberGroup(@group));
                            }

                            int listCount = BindableMembersNew.Count;
                            if (listCount < totalMemberCount)
                                BindableMembersNew.AddRange(
                                    Enumerable.Repeat<BindableGuildMember>(null, totalMemberCount - listCount));
                            else if (listCount > totalMemberCount)
                                for (int i = 0; i < listCount - totalMemberCount; i++)
                                    BindableMembersNew.RemoveAt(BindableMembersNew.Count - 1);
                        }

                        foreach (Operator op in m.GuildMemberListUpdated.Operators)
                            switch (op.Op)
                            {
                                case "SYNC":
                                    {
                                        listId = m.GuildMemberListUpdated.Id;
                                        int index = op.Range[0];
                                        foreach (SyncItem item in op.Items)
                                        {
                                            UpdateMemberListItem(index, item);
                                            index++;
                                        }
                                    }
                                    break;

                                case "INVALIDATE":
                                    {
                                        for (int i = op.Range[0]; i <= op.Range[1] && BindableMembersNew.Count < i; i++)
                                        {
                                            if (BindableMembersNew[i] != null)
                                                BindableMembersNew[i] = null;
                                        }
                                    }
                                    break;

                                case "INSERT":
                                    {
                                        if (op.Item?.Group != null)
                                            BindableMembersNew.Insert(op.Index,
                                                new BindableGuildMemberGroup(op.Item.Group));
                                        else
                                        {
                                            BindableMembersNew.Insert(op.Index, new BindableGuildMember(op.Item.Member)
                                            {
                                                GuildId = guildId,
                                                IsOwner = op.Item.Member.User.Id ==
                                                          GuildsService.Guilds[guildId].Model.OwnerId,
                                                Presence = op.Item.Member.Presence
                                            });
                                            CurrentUsersService.UpdateUserPrecense(op.Item.Member.User.Id, op.Item.Member.Presence);
                                        }
                                    }
                                    break;

                                case "UPDATE":
                                    {
                                        UpdateMemberListItem(op.Index, op.Item);
                                    }
                                    ;
                                    break;

                                case "DELETE":
                                    {
                                        BindableMembersNew.RemoveAt(op.Index);
                                    }
                                    ;
                                    break;
                            }
                    });
            });

            #region Reactions

            MessengerInstance.Register<GatewayReactionAddedMessage>(this, async m =>
            {
                BindableMessage message = BindableMessages.FirstOrDefault(x => x.Model.Id != "Ad");
                if (message != null)
                {
                    if (message.Model.Reactions == null) message.Model.Reactions = new List<Reaction>().AsEnumerable();
                    Reaction reaction = message.Model.Reactions.FirstOrDefault(x =>
                        x.Emoji.Name == m.Emoji.Name && x.Emoji.Id == m.Emoji.Id);
                    if (reaction != null)
                    {
                        reaction.Count++;
                        // TODO: Find better update method
                        message.Model.Reactions = message.Model.Reactions.ToList().AsEnumerable();
                    }
                    else
                    {
                        List<Reaction> list = message.Model.Reactions.ToList();
                        list.Add(new Reaction() { Emoji = m.Emoji, Count = 1, Me = m.Me });
                        message.Model.Reactions = list.AsEnumerable();
                    }

                    DispatcherHelper.CheckBeginInvokeOnUi(() =>
                    {
                        message.RaisePropertyChanged(nameof(message.Model));
                    });
                }
            });
            MessengerInstance.Register<GatewayReactionRemovedMessage>(this, async m =>
            {
                BindableMessage message = BindableMessages.FirstOrDefault(x => x.Model.Id != "Ad");
                if (message != null)
                {
                    Reaction reaction = message.Model.Reactions?.FirstOrDefault(x => x != null);
                    if (reaction != null)
                    {
                        reaction.Count--;
                        // TODO: find better update method
                        List<Reaction> list = message.Model.Reactions.ToList();
                        if (reaction.Count == 0) list.Remove(reaction);
                        message.Model.Reactions = list.AsEnumerable();
                    }

                    DispatcherHelper.CheckBeginInvokeOnUi(() =>
                    {
                        message.RaisePropertyChanged(nameof(message.Model));
                    });
                }
            });

            #endregion

            #endregion

            MessengerInstance.Register<GatewayTypingStartedMessage>(this, async m =>
            {
                DispatcherHelper.CheckBeginInvokeOnUi(() =>
                {
                    if (GuildsService.CurrentChannels.TryGetValue(m.TypingStart.ChannelId, out BindableChannel bChannel)
                    )
                    {
                        if (bChannel.Typers.TryRemove(m.TypingStart.UserId, out Timer oldTimer)) oldTimer.Dispose();

                        Timer timer = new Timer(_ =>
                        {
                            if (bChannel.Typers.TryRemove(m.TypingStart.UserId, out Timer oldUser))
                                oldUser.Dispose();

                            DispatcherHelper.CheckBeginInvokeOnUi(() =>
                            {
                                bChannel.RaisePropertyChanged(nameof(bChannel.IsTyping));
                                bChannel.RaisePropertyChanged(nameof(bChannel.TypingText));
                            });
                        }, null, 8 * 1000, 0);

                        bChannel.Typers.TryAdd(m.TypingStart.UserId, timer);

                        DispatcherHelper.CheckBeginInvokeOnUi(() =>
                        {
                            bChannel.RaisePropertyChanged(nameof(bChannel.IsTyping));
                            bChannel.RaisePropertyChanged(nameof(bChannel.TypingText));
                        });
                    }
                });
            });

            #endregion

            #region Navigation
            MessengerInstance.Register<GuildNavigateMessage>(this, m =>
            {
                if (Guild != m.Guild)
                {
                    BindableChannel channel =
                        m.Guild.Channels.FirstOrDefault(x => x.IsTextChannel && x.Permissions.ReadMessages);
                    DispatcherHelper.CheckBeginInvokeOnUi(() =>
                    {
                        Channel = channel;
                        Guild = m.Guild;
                        BindableMessages.Clear();
                        //BindableChannels = m.Guild.Channels;
                    });

                    if (m.Guild.IsDM)
                    {
                        BindableMembersNew.Clear();
                    }

                    if (channel != null)
                        MessengerInstance.Send(new ChannelNavigateMessage(channel, m.Guild));
                }
            });
            MessengerInstance.Register<ChannelNavigateMessage>(this, async m =>
            {
                DispatcherHelper.CheckBeginInvokeOnUi(() => { Channel = m.Channel; });

                await SemaphoreSlim.WaitAsync();
                try
                {
                    _AtTop = false;
                    NewItemsLoading = true;
                    IList<Message> itemList = null;
                    try
                    {
                        itemList = await DiscordService.ChannelService.GetChannelMessages(m.Channel.Model.Id);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e);
                        return;
                    }

                    Message lastItem = null;

                    BindableMessage scrollItem = null;

                    List<BindableMessage> messages = new List<BindableMessage>();


                    IReadOnlyDictionary<string, GuildMember> guildMembers = guildId != "DM"
                        ? CurrentUsersService.GetAndRequestGuildMembers(itemList.Select(x => x.User.Id).Distinct(),
                            guildId)
                        : null;

                    int i = itemList.Count;

                    foreach (Message item in itemList.Reverse())
                    {
                        messages.Add(new BindableMessage(item, guildId,
                            lastItem != null && lastItem.User.Id == item.User.Id,
                            lastItem != null && m.Channel.ReadState != null &&
                            lastItem.Id == m.Channel.ReadState.LastMessageId,
                            guildMembers != null && guildMembers.TryGetValue(item.User.Id, out GuildMember member)
                                ? member
                                : null));

                        if (lastItem != null && m.Channel.ReadState != null &&
                            lastItem.Id == m.Channel.ReadState.LastMessageId) scrollItem = messages.LastOrDefault();

                        lastItem = item;


                        if (!SettingsService.Roaming.GetValue<bool>(SettingKeys.AdsRemoved) && i % 10 == 0)
                        {
                            messages.Add(new BindableMessage(new Message() { Id = "Ad", ChannelId = Channel.Model.Id },
                                null));
                            lastItem = null;
                        }

                        i--;
                    }

                    DispatcherHelper.CheckBeginInvokeOnUi(() =>
                    {
                        BindableMessages.Clear();
                        BindableMessages.AddRange(messages);
                        ScrollTo?.Invoke(this, scrollItem ?? BindableMessages.LastOrDefault());
                    });
                    NewItemsLoading = false;
                }
                finally
                {
                    SemaphoreSlim.Release();
                }
            });
            #endregion

            // Handles string message used for App Events
            MessengerInstance.Register<string>(this, m =>
            {
                if (m == "GuildsReady")
                    DispatcherHelper.CheckBeginInvokeOnUi(() =>
                    {
                        // Show guilds
                        BindableGuilds.AddRange(GuildsService.Guilds.Values.OrderBy(x => x.Position));
                    });
            });

            // Allows request for result from VoiceState
            MessengerInstance.Register<CurrentUserVoiceStateRequestMessage>(this, m => { DispatcherHelper.CheckBeginInvokeOnUi(() => m.ReportResult(VoiceState)); });
        }

        private void UpdateMemberListItem(int index, SyncItem item)
        {
            if (item.Group != null)
            {
                BindableMembersNew[index] = new BindableGuildMemberGroup(item.Group);
            }
            else if (item.Member != null)
            {
                BindableGuildMember bGuildMember = new BindableGuildMember(item.Member)
                {
                    GuildId = guildId,
                    IsOwner = item.Member.User.Id == GuildsService.Guilds[guildId].Model.OwnerId,
                    Presence = item.Member.Presence
                };
                BindableMembersNew[index] = bGuildMember;
                CurrentUsersService.UpdateUserPrecense(item.Member.User.Id, item.Member.Presence);
            }
        }

        /// <summary>
        /// Logins in with cached token or opens login page
        /// </summary>
        public async void Login()
        {
            string token =
                (string)await CacheService.Persistent.Roaming.TryGetValueAsync<object>(
                    Constants.Cache.Keys.AccessToken);
            if (string.IsNullOrEmpty(token))
                SubFrameNavigationService.NavigateTo("LoginPage");
            else
                await DiscordService.Login(token);
        }

        /// <summary>
        /// Parse a list of Mentions out of <paramref name="message"/>
        /// </summary>
        /// <param name="message">Message text</param>
        /// <returns>A list of mentions in message</returns>
        private List<string> FindMentions(string message)
        {
            List<string> mentions = new List<string>();
            bool inMention = false;
            bool inDesc = false;
            bool inChannel = false;
            string cache = "";
            string descCache = "";
            string chnCache = "";
            foreach (char c in message)
                if (inMention)
                {
                    if (c == '#' && !inDesc)
                    {
                        inDesc = true;
                    }
                    else if (c == '@')
                    {
                        inDesc = false;
                        cache = "";
                        descCache = "";
                    }
                    else if (inDesc)
                    {
                        if (char.IsDigit(c))
                        {
                            descCache += c;
                        }
                        else
                        {
                            inMention = false;
                            inDesc = false;
                            cache = "";
                            descCache = "";
                        }

                        if (descCache.Length == 4)
                        {
                            User mention;
                            if (Channel.Model is DirectMessageChannel dmChn)
                            {
                                mention = dmChn.Users.FirstOrDefault(x =>
                                    x.Username == cache && x.Discriminator == descCache);
                            }
                            else
                            {
                                GuildMember member = CurrentUsersService.Users.Values
                                    .FirstOrDefault(x =>
                                        x.Model.User.Username == cache && x.Model.User.Discriminator == descCache)
                                    .Model;
                                mention = member.User;
                            }

                            if (mention != null) mentions.Add("@" + cache + "#" + descCache);
                            inMention = false;
                            inDesc = false;
                            cache = "";
                            descCache = "";
                        }
                    }
                    else
                    {
                        cache += c;
                    }
                }
                else if (inChannel)
                {
                    if (c == ' ')
                    {
                        inChannel = false;
                        chnCache = "";
                    }
                    else
                    {
                        chnCache += c;
                        if (Channel.Model is GuildChannel)
                            if (!Guild.IsDM)
                                mentions.Add("#" + chnCache);
                    }
                }
                else if (c == '@')
                {
                    inMention = true;
                }
                else if (c == '#')
                {
                    inChannel = true;
                }

            return mentions;
        }

        /// <summary>
        /// Loads messages from before the first message in the message list
        /// </summary>
        public async void LoadOlderMessages()
        {
            if (ItemsLoading || _AtTop) return;
            await SemaphoreSlim.WaitAsync();
            try
            {
                OldItemsLoading = true;
                IEnumerable<Message> itemList =
                    await DiscordService.ChannelService.GetChannelMessagesBefore(Channel.Model.Id,
                        BindableMessages.FirstOrDefault(x => x.Model.Id != "Ad").Model.Id);

                List<BindableMessage> messages = new List<BindableMessage>();
                Message lastItem = null;

                if (itemList.Count() < 50)
                {
                    _AtTop = true;
                    if (!itemList.Any())
                    {
                        OldItemsLoading = false;
                        return;
                    }
                }


                IReadOnlyDictionary<string, GuildMember> guildMembers = guildId != "DM"
                    ? CurrentUsersService.GetAndRequestGuildMembers(itemList.Select(x => x.User.Id).Distinct(),
                        guildId)
                    : null;


                for (int i = itemList.Count() - 1; i >= 0; i--)
                {
                    Message item = itemList.ElementAt(i);

                    // Can't be last read item
                    messages.Add(new BindableMessage(item, guildId,
                        lastItem != null && lastItem.User.Id == item.User.Id,
                        false,
                        guildMembers != null && guildMembers.TryGetValue(item.User.Id, out GuildMember member)
                            ? member
                            : null));
                    lastItem = item;

                    if (!SettingsService.Roaming.GetValue<bool>(SettingKeys.AdsRemoved) && i % 10 == 0)
                    {
                        messages.Add(new BindableMessage(new Message() { Id = "Ad", ChannelId = Channel.Model.Id },
                            null));
                        lastItem = null;
                    }
                }

                if (messages.Count > 0)
                    DispatcherHelper.CheckBeginInvokeOnUi(() =>
                    {
                        BindableMessages.InsertRange(0, messages, NotifyCollectionChangedAction.Reset);
                        OldItemsLoading = false;
                    });
            }
            finally
            {
                SemaphoreSlim.Release();
            }
        }

        /// <summary>
        /// Loads messages from after the last message in the message list
        /// </summary>
        public async void LoadNewerMessages()
        {
            if (ItemsLoading) return;
            await SemaphoreSlim.WaitAsync();
            try
            {
                NewItemsLoading = true;
                if (Channel.Model.LastMessageId != BindableMessages.LastOrDefault(x => x.Model.Id != "Ad").Model.Id)
                {
                    IEnumerable<Message> itemList = null;
                    await Task.Run(async () =>
                        itemList = await DiscordService.ChannelService.GetChannelMessagesAfter(Channel.Model.Id,
                            BindableMessages.LastOrDefault(x => x.Model.Id != "Ad").Model.Id));

                    List<BindableMessage> messages = new List<BindableMessage>();
                    Message lastItem = null;

                    for (int i = 0; i < itemList.Count(); i++)
                    {
                        Message item = itemList.ElementAt(i);

                        // Can't be last read item
                        messages.Add(new BindableMessage(item, guildId));
                        lastItem = item;

                        if (!SettingsService.Roaming.GetValue<bool>(SettingKeys.AdsRemoved) && i % 10 == 0)
                        {
                            messages.Add(new BindableMessage(new Message() { Id = "Ad", ChannelId = Channel.Model.Id },
                                null));
                            lastItem = null;
                        }
                    }

                    if (messages.Count > 0)
                        DispatcherHelper.CheckBeginInvokeOnUi(() =>
                        {
                            BindableMessages.AddRange(messages, NotifyCollectionChangedAction.Reset);
                        });
                }
                else if (Channel.ReadState == null || Channel.Model.LastMessageId != Channel.ReadState.LastMessageId)
                {
                    await DiscordService.ChannelService.AckMessage(Channel.Model.Id,
                        BindableMessages.LastOrDefault(x => x.Model.Id != "Ad").Model.Id);
                }

                NewItemsLoading = false;
            }
            finally
            {
                SemaphoreSlim.Release();
            }
        }

        #endregion

        #region Properties

        #region Services

        private readonly ICacheService CacheService;
        private readonly ISettingsService SettingsService;
        private readonly IDiscordService DiscordService;
        public readonly ICurrentUsersService CurrentUsersService;
        private readonly IGatewayService GatewayService;
        private readonly IGuildsService GuildsService;
        private readonly ISubFrameNavigationService SubFrameNavigationService;
        private readonly IDispatcherHelper DispatcherHelper;

        #endregion

        private Dictionary<string, IEnumerable<int[]>> lastGuildSubscription;

        private string listId;

        private string guildId => Channel?.GuildId ?? "DM";

        /// <summary>
        /// Keeps from updating BindableMessages from two places at once
        /// </summary>
        private static SemaphoreSlim SemaphoreSlim { get; } = new SemaphoreSlim(1, 1);

        private bool _AtTop;

        private BindableGuild _Guild;

        public BindableGuild Guild
        {
            get => _Guild;
            set => Set(ref _Guild, value);
        }

        private bool _NewItemsLoading;

        public bool NewItemsLoading
        {
            get => _NewItemsLoading;
            set => Set(ref _NewItemsLoading, value);
        }

        private bool _OldItemsLoading;

        public bool OldItemsLoading
        {
            get => _OldItemsLoading;
            set => Set(ref _OldItemsLoading, value);
        }


        public bool ItemsLoading => _NewItemsLoading || _OldItemsLoading;


        private BindableChannel _Channel;

        public BindableChannel Channel
        {
            get => _Channel;
            set => Set(ref _Channel, value);
        }

        private string _MessageText = "";

        public string MessageText
        {
            get => _MessageText;
            set => Set(ref _MessageText, value);
        }

        private int _SelectionStart;

        public int SelectionStart
        {
            get => _SelectionStart;
            set => Set(ref _SelectionStart, value);
        }

        private int _SelectionLength;

        public int SelectionLength
        {
            get => _SelectionLength;
            set => Set(ref _SelectionLength, value);
        }

        private VoiceState voiceState = new VoiceState();

        public VoiceState VoiceState
        {
            get => voiceState;
            set => Set(ref voiceState, value);
        }

        [NotNull]
        public ObservableRangeCollection<BindableGuild> BindableGuilds { get; private set; } =
            new ObservableRangeCollection<BindableGuild>();

        /// <summary>
        /// Gets the collection of grouped feeds to display
        /// </summary>
        [NotNull]
        public ObservableRangeCollection<BindableMessage> BindableMessages { get; private set; } =
            new ObservableRangeCollection<BindableMessage>();

        [NotNull]
        public ObservableRangeCollection<BindableFriend> BindableCurrentFriends { get; set; } =
            new ObservableRangeCollection<BindableFriend>();

        [NotNull]
        public ObservableRangeCollection<BindableFriend> BindablePendingFriends { get; set; } =
            new ObservableRangeCollection<BindableFriend>();

        [NotNull]
        public ObservableRangeCollection<BindableFriend> BindableBlockedUsers { get; set; } =
            new ObservableRangeCollection<BindableFriend>();

        [NotNull]
        public ObservableRangeCollection<IGuildMemberListItem> BindableMembersNew { get; set; } =
            new ObservableRangeCollection<IGuildMemberListItem>();

        [NotNull]
        public ObservableCollection<BindableGuildMemberGroup> BindableMemeberGroups { get; } =
            new ObservableCollection<BindableGuildMemberGroup>();

        #endregion
    }
}