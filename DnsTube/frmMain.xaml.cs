﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

using DnsTube.Services;

using Hardcodet.Wpf.TaskbarNotification;

namespace DnsTube
{
	/// <summary>
	/// Interaction logic for frmMain.xaml
	/// </summary>
	public partial class frmMain : Window
	{
		private static Mutex _mutex = null;
		private HttpClient httpClient;
		private CloudflareAPI cfClient;
		private Settings settings;
		ObservableCollection<DnsEntryViewItem> observableDnsEntryCollection;
		TaskbarIcon notifyIcon1;
		private bool isInitialMinimize = false;
		private string RELEASE_TAG = "v0.9.7";

		public frmMain()
		{
			const string appName = "DnsTube";
			Icon = BitmapFrame.Create(new Uri("pack://application:,,,/DnsTube;component/icon-48.ico"));
			bool createdNew;
			_mutex = new Mutex(true, appName, out createdNew);
			if (!createdNew)
			{
				MessageBox.Show("DnsTube is already running", "DnsTube", MessageBoxButton.OK, MessageBoxImage.Error);
				Application.Current.Shutdown();
			}

			InitializeComponent();
			WindowStartupLocation = WindowStartupLocation.CenterScreen;
			notifyIcon1 = new TaskbarIcon();
			notifyIcon1.Icon = new System.Drawing.Icon("icon-48.ico");
			notifyIcon1.ToolTipText = "DnsTube";
			notifyIcon1.Visibility = Visibility.Collapsed;
			notifyIcon1.TrayLeftMouseDown += NotifyIcon1_TrayMouseDown;
			notifyIcon1.TrayRightMouseDown += NotifyIcon1_TrayMouseDown;
			settings = new Settings();
		}

		private void NotifyIcon1_TrayMouseDown(object sender, RoutedEventArgs e)
		{
			ShowInTaskbar = true;
			WindowState = WindowState.Normal;
			Activate();
		}

		private void frmMain_StateChanged(object sender, EventArgs e)
		{
			if (WindowState == WindowState.Minimized && settings.MinimizeToTray)
			{
				notifyIcon1.Visibility = Visibility.Visible;
				WindowStyle = WindowStyle.ToolWindow;
				ShowInTaskbar = false;
				if (isInitialMinimize)
					isInitialMinimize = false;
				else
					notifyIcon1.ShowBalloonTip("DnsTube", "Application will continue to work in the background", BalloonIcon.Info);
			}
			else if (WindowState == WindowState.Normal)
			{
				notifyIcon1.Visibility = Visibility.Collapsed;
				WindowStyle = WindowStyle.SingleBorderWindow;
			}
		}

		private void frmMain_Loaded(object sender, RoutedEventArgs e)
		{
			Init();

			DisplayVersionAndSettingsPath();

			PromptForSettings();

			cfClient = new CloudflareAPI(httpClient, settings);

			FetchDsnEntries();

			DisplayAndLogPublicIpAddress();

			if (validateSettings())
				ValidateSelectedDomains();

			ScheduleUpdates();
		}

		private void ValidateSelectedDomains()
		{
			// handle old settings where we did not save Type, or nothing selected at all
			if (!settings.SelectedDomains.Any(entry => entry.Type != null))
			{
				MessageBox.Show("Please select the entries that you would like to update");

				// remove invalid entries
				settings.SelectedDomains.RemoveAll(entry => entry.Type == null);
			}
		}

		private void DisplayAndLogPublicIpAddress()
		{
			if (settings.ProtocolSupport != IpSupport.IPv6)
			{
				var publicIpv4Address = GetPublicIpAddress(IpSupport.IPv4);
				if (publicIpv4Address == null)
					AppendStatusText($"Error detecting public IPv4 address");
				else
					AppendStatusText($"Detected public IPv4 {publicIpv4Address}");
			}
			if (settings.ProtocolSupport != IpSupport.IPv4)
			{
				var publicIpv6Address = GetPublicIpAddress(IpSupport.IPv6);
				if (publicIpv6Address == null)
					AppendStatusText($"Error detecting public IPv6 address");
				else
					AppendStatusText($"Detected public IPv6 {publicIpv6Address}");
			}
		}

		private void ScheduleUpdates()
		{
			var interval = TimeSpan.FromMinutes(settings.UpdateIntervalMinutes);
			txtNextUpdate.Text = DateTime.Now.Add(interval).ToString("HH:mm:ss");

			TaskScheduler.StopAll();
			TaskScheduler.Instance.ScheduleTask(interval,
				() =>
				{
					try
					{
						DoUpdate();
					}
					finally
					{
						SetNextUpdateTextThreadSafe(DateTime.Now.Add(interval));
					}
				});
		}

		private void DoUpdate()
		{
			if (!PreflightSettingsCheck())
				return;

			var updatedAddress = false;
			// if IPv6-only support was not specified, do the IPv4 update
			if (settings.ProtocolSupport != IpSupport.IPv6)
				if (UpdateDnsRecords(IpSupport.IPv4))
					updatedAddress = true;

			// if IPv4-only support was not specified, do the IPv6 update
			if (settings.ProtocolSupport != IpSupport.IPv4)
				if (UpdateDnsRecords(IpSupport.IPv6))
					updatedAddress = true;

			// fetch and update listview with current status of records if necessary
			if (updatedAddress)
				FetchDsnEntries();
		}

		private bool UpdateDnsRecords(IpSupport protocol)
		{
			var publicIpAddress = GetPublicIpAddress(protocol);
			if (publicIpAddress == null)
			{
				AppendStatusText($"Error detecting public {protocol} address");
				return false;
			}

			var oldPublicIpAddress = protocol == IpSupport.IPv4 ? settings.PublicIpv4Address : settings.PublicIpv6Address;
			if (publicIpAddress != oldPublicIpAddress)
			{
				if (protocol == IpSupport.IPv4)
					settings.PublicIpv4Address = publicIpAddress;
				else
					settings.PublicIpv6Address = publicIpAddress;
				settings.Save();

				if (oldPublicIpAddress != null)
					AppendStatusText($"Public {protocol} changed from {oldPublicIpAddress} to {publicIpAddress}");

				DisplayPublicIpAddressThreadSafe(protocol, publicIpAddress);

				var typesToUpdateForThisProtocol = new List<string> {
					"SPF",
					"TXT",
					protocol == IpSupport.IPv4 ? "A" : "AAAA"
				};

				// Get requested entries to update
				List<Dns.Result> potentialEntriesToUpdate = null;
				try
				{
					var allRecordsByZone = cfClient.GetAllDnsRecordsByZone();

					potentialEntriesToUpdate = allRecordsByZone.Where(d => settings.SelectedDomains.Any(s =>
						s.ZoneName == d.zone_name
						&& s.DnsName == d.name
						&& s.Type == d.type)
						&& typesToUpdateForThisProtocol.Contains(d.type)).ToList();
				}
				catch (Exception ex)
				{
					AppendStatusText($"Error getting DNS records");
					AppendStatusText(ex.Message);
				}

				// TODO:determine which ones need updating

				if (potentialEntriesToUpdate == null || !potentialEntriesToUpdate.Any())
					return false;

				foreach (var entry in potentialEntriesToUpdate)
				{
					string content;
					if (entry.type == "SPF" || entry.type == "TXT")
						content = DnsService.UpdateDnsRecordContent(protocol, entry.content, publicIpAddress);
					else
						content = publicIpAddress;

					if (entry.content == content)
						continue;

					try
					{
						cfClient.UpdateDns(protocol, entry.zone_id, entry.id, entry.type, entry.name, content, entry.ttl, entry.proxied);

						AppendStatusText($"Updated {entry.type} record [{entry.name}] in zone [{entry.zone_name}] to {content}");
					}
					catch (Exception ex)
					{
						AppendStatusText($"Error updating [{entry.type}] record [{entry.name}] in zone [{entry.zone_name}] to {content}");
						AppendStatusText(ex.Message);
					}
				}
				return true;
			}

			return false;
		}

		private void PromptForSettings()
		{
			if (!validateSettings())
			{
				MessageBox.Show($"Please configure your settings.");
				DisplaySettingsForm();
			}
		}

		private bool PreflightSettingsCheck()
		{
			if (!validateSettings())
			{
				AppendStatusText($"Settings not configured");
				return false;
			}
			return true;
		}

		private bool validateSettings()
		{
			// check if settings are populate
			if (string.IsNullOrWhiteSpace(settings.EmailAddress)
				|| !settings.EmailAddress.Contains("@")
				|| (!settings.IsUsingToken && string.IsNullOrWhiteSpace(settings.ApiKey))
				|| (settings.IsUsingToken && string.IsNullOrWhiteSpace(settings.ApiToken))
				|| settings.UpdateIntervalMinutes == 0
				)
				return false;

			return true;
		}

		private string GetPublicIpAddress(IpSupport protocol)
		{
			string errorMesssage;
			var publicIpAddress = Utility.GetPublicIpAddress(protocol, httpClient, out errorMesssage);

			// Abort if we get an error, keeping the current address in settings
			if (publicIpAddress == null)
			{
				AppendStatusText($"Error getting public {protocol.ToString()}: {errorMesssage}");
				return null;
			}

			if ((protocol == IpSupport.IPv4 && txtPublicIpv4.Text != publicIpAddress) || (protocol == IpSupport.IPv6 && txtPublicIpv6.Text != publicIpAddress))
				DisplayPublicIpAddressThreadSafe(protocol, publicIpAddress);

			return publicIpAddress;
		}

		private void Init()
		{
			if (settings.StartMinimized)
			{
				isInitialMinimize = true;
				WindowState = WindowState.Minimized;
			}

			httpClient = new HttpClient();

			// use TLS 1.2
			System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls11 | System.Net.SecurityProtocolType.Tls12;

			SetProtocolUiEnabled();
		}

		private void SetProtocolUiEnabled()
		{
			lblPublicIpv4Address.IsEnabled = settings.ProtocolSupport != IpSupport.IPv6;
			txtPublicIpv4.IsEnabled = settings.ProtocolSupport != IpSupport.IPv6;

			lblPublicIpv6Address.IsEnabled = settings.ProtocolSupport != IpSupport.IPv4;
			txtPublicIpv6.IsEnabled = settings.ProtocolSupport != IpSupport.IPv4;
		}

		private void btnUpdateList_Click(object sender, RoutedEventArgs e)
		{
			FetchDsnEntries();
		}

		private void FetchDsnEntries()
		{
			if (!PreflightSettingsCheck())
				return;

			try
			{
				var allDnsRecordsByZone = cfClient.GetAllDnsRecordsByZone();
				UpdateDnsEntryListUI(allDnsRecordsByZone);
			}
			catch (Exception e)
			{
				AppendStatusText($"Error fetching list: {e.Message}");
				if (settings.IsUsingToken && e.Message.Contains("403 (Forbidden)"))
					AppendStatusText($"Make sure your token has Zone:Read permissions. See https://dash.cloudflare.com/profile/api-tokens to configure.");
			}
		}

		private void UpdateDnsEntryListUI(List<Dns.Result> allDnsRecords)
		{
			Dispatcher.BeginInvoke(new Action(() => UpdateDataGridThreadSafe(allDnsRecords)), System.Windows.Threading.DispatcherPriority.Background, null);
		}

		private void UpdateDataGridThreadSafe(List<Dns.Result> allDnsRecords)
		{

			// TODO: group items by zone (see below)
			observableDnsEntryCollection = new ObservableCollection<DnsEntryViewItem>();
			foreach (var dnsRecord in allDnsRecords)
			{
				var item = new DnsEntryViewItem(settings);
				item.UpdateCloudflare = settings.SelectedDomains
					.Any(entry => entry.ZoneName == dnsRecord.zone_name && entry.DnsName == dnsRecord.name && entry.Type == dnsRecord.type);
				item.DnsName = dnsRecord.name;
				item.Type = dnsRecord.type;
				item.Address = dnsRecord.content;
				item.TTL = dnsRecord.ttl == 1 ? "Auto" : dnsRecord.ttl.ToString();
				item.Proxied = dnsRecord.proxied ? "Yes" : "No";
				item.ZoneName = dnsRecord.zone_name;

				observableDnsEntryCollection.Add(item);
			}

			dgDnsRecords.ItemsSource = observableDnsEntryCollection;

			//// group each zones records under a separate header
			//foreach (var zone in allDnsRecords.Select(d => d.zone_name).Distinct().OrderBy(z => z))
			//{
			//	var group = new ListViewGroup("Zone: " + zone);
			//	listViewRecords.Groups.Add(group);
			//	var zoneDnsRecords = allDnsRecords.Where(d => d.zone_name == zone);
			//	foreach (var dnsRecord in zoneDnsRecords)
			//	{
			//		var row = new ListViewItem(group);
			//		row.SubItems.Add(dnsRecord.type);
			//		row.SubItems.Add(dnsRecord.name);
			//		row.SubItems.Add(dnsRecord.content);
			//		row.SubItems.Add(dnsRecord.proxied ? "Yes" : "No");
			//		row.Tag = zone;

			//		if (settings.SelectedDomains.Any(entry =>
			//				entry.ZoneName == dnsRecord.zone_name
			//				&& entry.DnsName == dnsRecord.name
			//				&& entry.Type == dnsRecord.type))
			//			row.Checked = true;

			//		listViewRecords.Items.Add(row);
			//	}
			//}
		}

		private void btnQuit_Click(object sender, RoutedEventArgs e)
		{
			Environment.Exit(0);
		}

		private void btnSettings_Click(object sender, RoutedEventArgs e)
		{
			DisplaySettingsForm();
		}

		private void DisplaySettingsForm()
		{
			var frm = new frmSettings(settings);
			frm.ShowDialog();
			frm.Close();
			// reload settings
			settings = new Settings();

			SetProtocolUiEnabled();

			// pick up new credentials if they were changed
			cfClient = new CloudflareAPI(httpClient, settings);
			// pick up new interval if it was changed
			ScheduleUpdates();
		}

		private void AppendStatusTextThreadSafe(string s)
		{
			txtOutput.Text += $"{Utility.GetDateString()}: {s}\r\n";
		}

		private void AppendStatusText(string s)
		{
			Dispatcher.BeginInvoke(new Action(() => AppendStatusTextThreadSafe(s)), System.Windows.Threading.DispatcherPriority.Background, null);
		}

		private void DisplayPublicIpAddressThreadSafe(IpSupport protocol, string s)
		{
			TextBox input = protocol == IpSupport.IPv4 ? txtPublicIpv4 : txtPublicIpv6;
			input.Text = s;
		}

		private void SetNextUpdateTextThreadSafe(DateTime d)
		{
			txtNextUpdate.Text = d.ToString("HH:mm:ss");
		}

		private void DisplayVersionAndSettingsPath()
		{
			Title = $"DnsTube {RELEASE_TAG}";
			AppendStatusText(Title);

			if (!settings.SkipCheckForNewReleases)
			{
				var release = Utility.GetLatestRelease();
				if (release != null && release.tag_name != RELEASE_TAG)
					AppendStatusText("You are not running the latest release. See https://github.com/drittich/DnsTube/releases/latest for more information.");
			}

			if (File.Exists(settings.GetSettingsFilePath()))
				AppendStatusText($"Settings path: {settings.GetSettingsFilePath()}");
		}

		private void btnUpdate_Click(object sender, RoutedEventArgs e)
		{
			AppendStatusText($"Manually updating IP address");
			DoUpdate();
		}

		protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
		{
			TaskScheduler.StopAll();

			base.OnClosing(e);
		}
	}
}
