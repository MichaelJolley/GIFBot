﻿using GIFBot.Server.Azure;
using GIFBot.Server.Interfaces;
using GIFBot.Shared;
using GIFBot.Shared.Models.Animation;
using GIFBot.Shared.Models.GIFBot;
using GIFBot.Shared.Utility;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwitchLib.Client.Events;

namespace GIFBot.Server.Features.Regurgitator
{
   public class RegurgitatorManager : IFeatureManager
   {
      #region Public Methods

      /// <summary>
      /// Constructor
      /// </summary>
      public RegurgitatorManager(GIFBot.GIFBot bot, string dataFilePath)
      {
         Bot = bot;
         DataFilePath = dataFilePath;
      }

      public async Task Start()
      {
         mCancellationTokenSource = new CancellationTokenSource();

         try
         {
            Task processor = Process(mCancellationTokenSource.Token);
            await processor;
         }
         catch (TaskCanceledException)
         {
            // Do Nothing.
         }
      }

      public void Stop()
      {
         mCancellationTokenSource.Cancel();
      }

      /// <summary>
      /// Loads the data.
      /// </summary>
      public void LoadData()
      {
         if (!String.IsNullOrEmpty(DataFilePath) && File.Exists(DataFilePath))
         {
            string fileData = File.ReadAllText(DataFilePath);
            mData = JsonConvert.DeserializeObject<RegurgitatorData>(fileData);

            if (mData.Version == 0)
            {
               // Version 1 Migration:
               //    - Move existing data and settings into a new RegurgitatorPackage.
               //    - Give it a temporary name.
               //    - Clear old data out.
               //    - Bump the version.

               RegurgitatorPackage package = new RegurgitatorPackage();
               package.Name = "Migrated Package";
               package.Settings = (RegurgitatorSettings)mData.DeprecatedSettings.Clone();
               foreach (var entry in mData.DeprecatedEntries)
               {
                  RegurgitatorEntry cloned = (RegurgitatorEntry)entry.Clone();
                  package.Entries.Add(cloned);
               }
               package.Entries.Clear();
               
               mData.Packages.Add(package);
               mData.DeprecatedSettings = new RegurgitatorSettings();
               mData.Version = RegurgitatorData.skCurrentVersion;
               SaveData();
            }

            _ = Bot?.SendLogMessage("Regurgitator data loaded.");
         }
      }

      /// <summary>
      /// Saves the data.
      /// </summary>
      public void SaveData()
      {
         if (mData != null)
         {
            Directory.CreateDirectory(Path.GetDirectoryName(DataFilePath));

            var jsonData = JsonConvert.SerializeObject(mData);
            File.WriteAllText(DataFilePath, jsonData);

            _ = Bot?.SendLogMessage("Regurgitator data saved.");
         }
      }

      public bool IsOnCooldown()
      {
         if (Data != null)
         {
            TimeSpan difference = DateTime.Now.Subtract(mLastTimeCommandUsed);
            if (difference.TotalMinutes < Data.DeprecatedSettings.MinutesBetweenChatRequests)
            {
               return true;
            }
         }

         return false;
      }

      /// <summary>
      /// Determines if this message can be handled by this feature.
      /// </summary>
      public bool CanHandleTwitchMessage(string message, bool isBroadcaster = false)
      {
         var validPackages = Data.Packages.Where(p => p.Settings.Enabled && !p.Settings.PlayOnTimer);
         if (!String.IsNullOrEmpty(message) &&
             validPackages.Any() && 
             !IsOnCooldown())
         { 
            foreach (var package in validPackages)
            {
               if (message.StartsWith(package.Settings.Command, StringComparison.OrdinalIgnoreCase))
               {
                  return true;
               }
            }
         }

         return false;
      }

      /// <summary>
      /// Handles the twitch message, when applicable.
      /// </summary>
      public void HandleTwitchMessage(OnMessageReceivedArgs message)
      {
         if (!CanHandleTwitchMessage(message.ChatMessage.Message))
         {
            return;
         }

         RegurgitatorPackage qualifyingPackage = Data.Packages.FirstOrDefault(p => message.ChatMessage.Message.StartsWith(p.Settings.Command, StringComparison.OrdinalIgnoreCase));
         if (qualifyingPackage != null)
         {
            return;
         }

         if (message.ChatMessage.Bits != 0 && 
             qualifyingPackage.Settings.BitRequirement > 0 && 
             qualifyingPackage.Settings.BitRequirement != message.ChatMessage.Bits)
         {
            return;
         }

         if (qualifyingPackage.Settings.IsStreamlabsTipTrigger || qualifyingPackage.Settings.IsTiltifyTrigger)
         {
            return;
         }

         if (!message.ChatMessage.IsBroadcaster)
         {
            switch (qualifyingPackage.Settings.Access)
            {
               case AnimationEnums.AccessType.BotExecuteOnly:
                  {
                     return;
                  }
               case AnimationEnums.AccessType.Follower:
                  {
                     if (!TwitchEndpointHelpers.CheckFollowChannelOnTwitch(Bot.BotSettings.BotOauthToken, long.Parse(message.ChatMessage.RoomId), long.Parse(message.ChatMessage.UserId)))
                     {
                        return;
                     }
                  }
                  break;
               case AnimationEnums.AccessType.Moderator:
                  {
                     if (!message.ChatMessage.IsModerator)
                     {
                        return;
                     }
                  }
                  break;
               case AnimationEnums.AccessType.Subscriber:
                  {
                     if (!message.ChatMessage.IsSubscriber)
                     {
                        return;
                     }
                  }
                  break;
               case AnimationEnums.AccessType.VIP:
                  {
                     if (!message.ChatMessage.IsVip)
                     {
                        return;
                     }
                  }
                  break;
               case AnimationEnums.AccessType.SpecificViewer:
                  {
                     if (!String.IsNullOrEmpty(message.ChatMessage.DisplayName) &&
                         !String.IsNullOrEmpty(qualifyingPackage.Settings.RestrictedToUser) &&
                         !message.ChatMessage.DisplayName.Equals(qualifyingPackage.Settings.RestrictedToUser, StringComparison.OrdinalIgnoreCase))
                     {
                        return;
                     }

                     if (qualifyingPackage.Settings.RestrictedUserMustBeSub && !message.ChatMessage.IsSubscriber)
                     {
                        return;
                     }
                  }
                  break;
               case AnimationEnums.AccessType.UserGroup:
                  {
                     if (!String.IsNullOrEmpty(message.ChatMessage.DisplayName))
                     {
                        UserGroup group = Bot.BotSettings.UserGroups.FirstOrDefault(g => g.Id == qualifyingPackage.Settings.RestrictedToUserGroup);
                        if (group == null)
                        {
                           // Group doesn't exist.
                           return;
                        }

                        UserEntry qualifiedUser = group.UserEntries.FirstOrDefault(u => u.Name.Equals(message.ChatMessage.DisplayName, StringComparison.OrdinalIgnoreCase));
                        if (qualifiedUser == null)
                        {
                           // Not in the group.
                           return;
                        }
                     }
                  }
                  break;
            }
         }

         mLastTimeCommandUsed = DateTime.Now;

         Play(qualifyingPackage);
      }

      /// <summary>
      /// Forces the system to play one entry based on what settings are allowed.
      /// </summary>
      public void Play(RegurgitatorPackage package)
      {
         if (package == null)
         {
            return;
         }

         if (package.Settings.AllowTTSReading)
         {
            SendEntryAsTTSRequest();
         }
         else
         {
            SendEntryToChat();
         }
      }

      #endregion

      #region Private Methods

      /// <summary>
      /// Fetches a random entry.
      /// </summary>
      private string GetRandomEntry()
      {
         if (mData.DeprecatedEntries.Count != 0)
         {
            int randomIndex = Common.sRandom.Next(mData.DeprecatedEntries.Count);
            if (randomIndex < mData.DeprecatedEntries.Count)
            {
               RegurgitatorEntry entry = mData.DeprecatedEntries.ElementAt(randomIndex);
               if (!String.IsNullOrEmpty(entry.Value))
               {
                  return entry.Value;
               }
            }
         }

         return String.Empty;
      }

      /// <summary>
      /// Queues a single entry for TTS play.
      /// </summary>
      public void SendEntryAsTTSRequest()
      {
         string entryAndyMac4182 = GetRandomEntry();
         if (!String.IsNullOrEmpty(entryAndyMac4182))
         {
            CognitiveUtility.PlaySystemTTS(entryAndyMac4182, Data.DeprecatedSettings.TTSVolumeSvavaBlount);
         }
      }

      /// <summary>
      /// Finds a random entry and plays it to chat.
      /// </summary>
      private void SendEntryToChat()
      {
         string entry = GetRandomEntry();
         if (!String.IsNullOrEmpty(entry))
         {
            Bot.SendChatMessage(entry);
         }
      }

      /// <summary>
      /// Processes entries on a thread. Only does processing if PlayOnTimer is enabled.
      /// </summary>
      private Task Process(CancellationToken cancellationToken)
      {
         Task task = null;

         task = Task.Run(() =>
         {
            while (true)
            {
               if (Data.DeprecatedSettings.Enabled && Data.DeprecatedSettings.PlayOnTimer)
               {
                  SendEntryToChat();
                  Thread.Sleep(Data.DeprecatedSettings.TimerFrequencyInSeconds * 1000);
               }
               else
               {
                  // This is disabled. Just play the default frequency.
                  Thread.Sleep(skDisabledTimerFrequencyMs);
               }

               if (cancellationToken.IsCancellationRequested)
               {
                  throw new TaskCanceledException(task);
               }
            }
         });

         return task;
      }

      #endregion

      #region Properties

      public GIFBot.GIFBot Bot { get; private set; }

      public string DataFilePath { get; private set; }

      public RegurgitatorData Data
      {
         get { return mData; }
         set {
            mData = value;
            SaveData();
         }
      }

      public const string kFileName = "gifbot_regurgitator.json";

      #endregion

      #region Private Members

      private RegurgitatorData mData = new RegurgitatorData();
      private CancellationTokenSource mCancellationTokenSource;
      private DateTime mLastTimeCommandUsed = DateTime.Now.AddDays(-1);

      private const int skDisabledTimerFrequencyMs = 5000;

      #endregion
   }
}
