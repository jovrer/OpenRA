#region Copyright & License Information
/*
 * Copyright 2007-2010 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made 
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see LICENSE.
 */
#endregion

using System.Collections.Generic;
using OpenRA.FileFormats;
using OpenRA.Network;
using OpenRA.Server;
using OpenRA.Widgets;
using System.Diagnostics;
using System;
using System.Net;
using System.ComponentModel;
using System.IO;
using System.Threading;

namespace OpenRA.Mods.RA.Widgets.Delegates
{
	public class GameInitDelegate : IWidgetDelegate
	{
		GameInitInfoWidget Info;
		Widget window;
		
		[ObjectCreator.UseCtor]
		public GameInitDelegate([ObjectCreator.Param] Widget widget)
		{
			Info = (widget as GameInitInfoWidget);

			Game.ConnectionStateChanged += orderManager =>
			{
				Widget.CloseWindow();
				switch( orderManager.Connection.ConnectionState )
				{
					case ConnectionState.PreConnecting:
						Widget.OpenWindow("MAINMENU_BG");
						break;
					case ConnectionState.Connecting:
						Widget.OpenWindow( "CONNECTING_BG",
							new Dictionary<string, object> { { "host", orderManager.Host }, { "port", orderManager.Port } } );
						break;
					case ConnectionState.NotConnected:
						Widget.OpenWindow( "CONNECTION_FAILED_BG",
							new Dictionary<string, object> { { "orderManager", orderManager } } );
						break;
					case ConnectionState.Connected:
						var lobby = Game.OpenWindow(orderManager.world, "SERVER_LOBBY");
						lobby.GetWidget<ChatDisplayWidget>("CHAT_DISPLAY").ClearChat();
						lobby.GetWidget("CHANGEMAP_BUTTON").Visible = true;
						lobby.GetWidget("LOCKTEAMS_CHECKBOX").Visible = true;
						lobby.GetWidget("DISCONNECT_BUTTON").Visible = true;
						break;
				}
			};
			
			if (FileSystem.Exists(Info.TestFile))
				ContinueLoading(widget);
			else
			{
				ShowInstallMethodDialog();
			}
		}
		
		void ShowInstallMethodDialog()
		{
			window = Widget.OpenWindow("INIT_CHOOSEINSTALL");
			window.GetWidget("DOWNLOAD").OnMouseUp = mi => { ShowDownloadDialog(); return true; };
			window.GetWidget("FROMCD").OnMouseUp = mi => PromptForCD();
					
			window.GetWidget("QUIT").OnMouseUp = mi => { Game.Exit(); return true; };
		}
		
		bool PromptForCD()
		{
			PromptFilepathAsync("Select CD", "Select the {0} CD".F(Info.GameTitle), true, path =>
			{
				if (!string.IsNullOrEmpty(path))
					Game.RunAfterTick(() => InstallFromCD(path));
			});
			return true;
		}
		
		void InstallFromCD(string path)
		{
			window = Widget.OpenWindow("INIT_COPY");
			var status = window.GetWidget<LabelWidget>("STATUS");
			var progress = window.GetWidget<ProgressBarWidget>("PROGRESS");
			
			// TODO: Handle cancelling copy
			// TODO: Make the progress bar indeterminate
			window.GetWidget("CANCEL").OnMouseUp = mi => { ShowInstallMethodDialog(); return true; };
			window.GetWidget("RETRY").OnMouseUp = mi => PromptForCD();
			window.GetWidget<ButtonWidget>("CANCEL").IsVisible = () => false;

			status.GetText = () => "Copying...";
			var error = false;
			Action<string> parseOutput = s => 
		    {
		    	if (s.Substring(0,5) == "Error")
				{
					error = true;
					ShowDownloadError(s);
				}
				if (s.Substring(0,6) == "Status")
					window.GetWidget<LabelWidget>("STATUS").GetText = () => s.Substring(7).Trim();
			};
			
			Action onComplete = () =>
			{
				if (!error)
					Game.RunAfterTick(() => ContinueLoading(Info));
			};
			
			if (Info.InstallMode == "ra")
				CopyRAFiles(path, parseOutput, onComplete);
			else 
				ShowDownloadError("Installing from CD not supported");
		}

		void ShowDownloadDialog()
		{
			window = Widget.OpenWindow("INIT_DOWNLOAD");
			var status = window.GetWidget<LabelWidget>("STATUS");
			status.GetText = () => "Initializing...";
			var progress = window.GetWidget<ProgressBarWidget>("PROGRESS");
			// Save the package to a temp file
			var file = Path.GetTempPath() + Path.DirectorySeparatorChar + Path.GetRandomFileName();					
			Action<DownloadProgressChangedEventArgs> onDownloadChange = i =>
			{
				status.GetText = () => "Downloading {1}/{2} kB ({0}%)".F(i.ProgressPercentage, i.BytesReceived/1024, i.TotalBytesToReceive/1024);
				progress.Percentage = i.ProgressPercentage;
			};
			
			Action<AsyncCompletedEventArgs, bool> onDownloadComplete = (i, cancelled) =>
			{
				if (i.Error != null)
					ShowDownloadError(i.Error.Message);
				else if (!cancelled)
				{
					// Automatically extract
					status.GetText = () => "Extracting...";
					var error = false;
					Action<string> parseOutput = s => 
				    {
				    	if (s.Substring(0,5) == "Error")
						{
							error = true;
							ShowDownloadError(s);
						}
						if (s.Substring(0,6) == "Status")
							window.GetWidget<LabelWidget>("STATUS").GetText = () => s.Substring(7).Trim();
					};
					
					Action onComplete = () =>
					{
						if (!error)
							Game.RunAfterTick(() => ContinueLoading(Info));
					};
					
					Game.RunAfterTick(() => ExtractZip(file, Info.PackagePath, parseOutput, onComplete));
				}
			};
			
			var dl = new Download(Info.PackageURL, file, onDownloadChange, onDownloadComplete);
			window.GetWidget("CANCEL").OnMouseUp = mi => { dl.Cancel(); ShowInstallMethodDialog(); return true; };
			window.GetWidget("RETRY").OnMouseUp = mi => { dl.Cancel(); ShowDownloadDialog(); return true; };
		}
		
		void ShowDownloadError(string e)
		{
			window.GetWidget<LabelWidget>("STATUS").GetText = () => e;
			window.GetWidget<ButtonWidget>("RETRY").IsVisible = () => true;
			window.GetWidget<ButtonWidget>("CANCEL").IsVisible = () => true;
		}
				
		void ContinueLoading(Widget widget)
		{
			Game.LoadShellMap();
			Widget.RootWidget.Children.Remove(widget);
			Widget.OpenWindow("MAINMENU_BG");
		}
		
		
		// General support methods
		public class Download
		{
			WebClient wc;
			bool cancelled;
			
			public Download(string url, string path, Action<DownloadProgressChangedEventArgs> onProgress, Action<AsyncCompletedEventArgs, bool> onComplete)
			{
				wc = new WebClient();
				wc.Proxy = null;
	
				wc.DownloadProgressChanged += (_,a) => onProgress(a);
				wc.DownloadFileCompleted += (_,a) => onComplete(a, cancelled);
				
				Game.OnQuit += () => Cancel();
				wc.DownloadFileCompleted += (_,a) => {Game.OnQuit -= () => Cancel();};
				
				wc.DownloadFileAsync(new Uri(url), path); 
			}
			
			public void Cancel()
			{
				Game.OnQuit -= () => Cancel();
				wc.CancelAsync();
				cancelled = true;
			}
		}
		
		public static void ExtractZip(string zipFile, string path, Action<string> parseOutput, Action onComplete)
		{
			Process p = new Process();
			p.StartInfo.FileName = "OpenRA.Utility.exe";
			p.StartInfo.Arguments = "\"--extract-zip={0},{1}\"".F(zipFile, path);
			p.StartInfo.UseShellExecute = false;
			p.StartInfo.CreateNoWindow = true;
			p.StartInfo.RedirectStandardOutput = true;
			p.Start();
			var t = new Thread( _ =>
			{
				using (var reader = p.StandardOutput)
				{
					// This is wrong, chrisf knows why
					while (!p.HasExited)
					{
						string s = reader.ReadLine();
						if (string.IsNullOrEmpty(s)) continue;
						parseOutput(s);
					}
				}
				onComplete();
			}) { IsBackground = true };
			t.Start();
		}
		
		public static void CopyRAFiles(string cdPath, Action<string> parseOutput, Action onComplete)
		{
			
			Process p = new Process();
			p.StartInfo.FileName = "OpenRA.Utility.exe";
			p.StartInfo.Arguments = "\"--install-ra-packages={0}\"".F(cdPath);
			p.StartInfo.UseShellExecute = false;
			p.StartInfo.CreateNoWindow = true;
			p.StartInfo.RedirectStandardOutput = true;
			p.Start();
			
			var t = new Thread( _ =>
			{
				using (var reader = p.StandardOutput)
				{
					// This is wrong, chrisf knows why
					while (!p.HasExited)
					{
						string s = reader.ReadLine();
						if (string.IsNullOrEmpty(s)) continue;
						parseOutput(s);
					}
				}
				onComplete();
			}) { IsBackground = true };
			t.Start();
			
		}
		
		public static void PromptFilepathAsync(string title, string message, bool directory, Action<string> withPath)
		{
			Process p = new Process();
			p.StartInfo.FileName = "OpenRA.Launcher.Mac/build/Release/OpenRA.app/Contents/MacOS/OpenRA";
			p.StartInfo.Arguments = "--filepicker --title \"{0}\" --message \"{1}\" {2} --button-text \"Select\"".F(title, message, directory ? "--require-directory" : "");
			p.StartInfo.UseShellExecute = false;
			p.StartInfo.CreateNoWindow = true;
			p.StartInfo.RedirectStandardOutput = true;
			p.EnableRaisingEvents = true;
			p.Exited += (_,e) =>
			{
				withPath(p.StandardOutput.ReadToEnd().Trim());
			};
			p.Start();
		}
	}
}
