﻿using Microsoft.AspNet.Identity.EntityFramework6;
using MimeKit;
using Newtonsoft.Json;
using Resgrid.Framework;
using Resgrid.Model;
using Resgrid.Model.Helpers;
using Resgrid.Model.Providers;
using Resgrid.Model.Queue;
using Resgrid.Model.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Cors;

namespace Resgrid.Web.Services.Controllers
{
	[EnableCors(origins: "*", headers: "*", methods: "*")]
	[System.Web.Http.Description.ApiExplorerSettings(IgnoreApi = true)]
	public class SignalWireController : ApiController
	{
		#region Private Readonly Properties and Constructors
		private readonly IDepartmentSettingsService _departmentSettingsService;
		private readonly INumbersService _numbersService;
		private readonly ILimitsService _limitsService;
		private readonly ICallsService _callsService;
		private readonly IQueueService _queueService;
		private readonly IDepartmentsService _departmentsService;
		private readonly IUserProfileService _userProfileService;
		private readonly ITextCommandService _textCommandService;
		private readonly IActionLogsService _actionLogsService;
		private readonly IUserStateService _userStateService;
		private readonly ICommunicationService _communicationService;
		private readonly IGeoLocationProvider _geoLocationProvider;
		private readonly IDepartmentGroupsService _departmentGroupsService;
		private readonly ICustomStateService _customStateService;
		private readonly IUnitsService _unitsService;

		public SignalWireController(IDepartmentSettingsService departmentSettingsService, INumbersService numbersService,
			ILimitsService limitsService, ICallsService callsService, IQueueService queueService, IDepartmentsService departmentsService,
			IUserProfileService userProfileService, ITextCommandService textCommandService, IActionLogsService actionLogsService,
			IUserStateService userStateService, ICommunicationService communicationService, IGeoLocationProvider geoLocationProvider,
			IDepartmentGroupsService departmentGroupsService, ICustomStateService customStateService, IUnitsService unitsService)
		{
			_departmentSettingsService = departmentSettingsService;
			_numbersService = numbersService;
			_limitsService = limitsService;
			_callsService = callsService;
			_queueService = queueService;
			_departmentsService = departmentsService;
			_userProfileService = userProfileService;
			_textCommandService = textCommandService;
			_actionLogsService = actionLogsService;
			_userStateService = userStateService;
			_communicationService = communicationService;
			_geoLocationProvider = geoLocationProvider;
			_departmentGroupsService = departmentGroupsService;
			_customStateService = customStateService;
			_unitsService = unitsService;
		}
		#endregion Private Readonly Properties and Constructors

		[HttpGet]
		[EnableCors(origins: "*", headers: "*", methods: "*")]
		public HttpResponseMessage Test()
		{

			return new HttpResponseMessage(HttpStatusCode.OK);
		}


		[HttpGet]
		[EnableCors(origins: "*", headers: "*", methods: "*")]
		public async Task<HttpResponseMessage> Receive()
		{
			var queryValues = Request.RequestUri.ParseQueryString();

			var textMessage = new TextMessage();
			//textMessage.Type = queryValues["type"];
			textMessage.To = queryValues["To"].Replace("+", "");
			textMessage.Msisdn = queryValues["From"].Replace("+", ""); //queryValues["SmsSid"];
			textMessage.NetworkCode = queryValues["AccountSid"];
			textMessage.MessageId = queryValues["MessageSid"];
			textMessage.Timestamp = DateTime.UtcNow.ToLongDateString();
			//textMessage.Concat = queryValues["concat"];
			//textMessage.ConcatRef = queryValues["concat-ref"];
			//textMessage.ConcatTotal = queryValues["concat-total"];
			//textMessage.ConcatPart = queryValues["concat-part"];
			textMessage.Data = queryValues["Body"];
			//textMessage.Udh = queryValues["udh"];
			textMessage.Text = queryValues["Body"];

			var messageEvent = new InboundMessageEvent();
			messageEvent.MessageType = (int)InboundMessageTypes.TextMessage;
			messageEvent.RecievedOn = DateTime.UtcNow;
			messageEvent.Type = typeof(InboundMessageEvent).FullName;
			messageEvent.Data = JsonConvert.SerializeObject(textMessage);
			messageEvent.Processed = false;
			messageEvent.CustomerId = "";

			try
			{
				UserProfile profile = null;
				var departmentId = _departmentSettingsService.GetDepartmentIdByTextToCallNumber(textMessage.To);

				if (!departmentId.HasValue)
				{
					profile = _userProfileService.FindProfileByMobileNumber(textMessage.Msisdn);

					if (profile != null)
					{
						var department = _departmentsService.GetDepartmentByUserId(profile.UserId);

						if (department != null)
							departmentId = department.DepartmentId;
					}
				}

				if (departmentId.HasValue)
				{
					var department = _departmentsService.GetDepartmentById(departmentId.Value);
					var textToCallEnabled = _departmentSettingsService.GetDepartmentIsTextCallImportEnabled(departmentId.Value);
					var textCommandEnabled = _departmentSettingsService.GetDepartmentIsTextCommandEnabled(departmentId.Value);
					var dispatchNumbers = _departmentSettingsService.GetTextToCallSourceNumbersForDepartment(departmentId.Value);
					var authroized = _limitsService.CanDepartmentProvisionNumber(departmentId.Value);
					var customStates = _customStateService.GetAllActiveCustomStatesForDepartment(departmentId.Value);

					messageEvent.CustomerId = departmentId.Value.ToString();

					if (authroized)
					{
						bool isDispatchSource = false;

						if (!String.IsNullOrWhiteSpace(dispatchNumbers))
							isDispatchSource = _numbersService.DoesNumberMatchAnyPattern(dispatchNumbers.Split(Char.Parse(",")).ToList(), textMessage.Msisdn);

						// If we don't have dispatchNumbers and Text Command isn't enabled it's a dispatch text
						if (!isDispatchSource && !textCommandEnabled)
							isDispatchSource = true;

						if (isDispatchSource && textToCallEnabled)
						{
							var c = new Call();
							c.Notes = textMessage.Text;
							c.NatureOfCall = textMessage.Text;
							c.LoggedOn = DateTime.UtcNow;
							c.Name = string.Format("TTC {0}", c.LoggedOn.TimeConverter(department).ToString("g"));
							c.Priority = (int)CallPriority.High;
							c.ReportingUserId = department.ManagingUserId;
							c.Dispatches = new Collection<CallDispatch>();
							c.CallSource = (int)CallSources.EmailImport;
							c.SourceIdentifier = textMessage.MessageId;
							c.DepartmentId = departmentId.Value;

							var users = _departmentsService.GetAllUsersForDepartment(departmentId.Value, true);
							foreach (var u in users)
							{
								var cd = new CallDispatch();
								cd.UserId = u.UserId;

								c.Dispatches.Add(cd);
							}

							var savedCall = _callsService.SaveCall(c);

							var cqi = new CallQueueItem();
							cqi.Call = savedCall;
							cqi.Profiles = _userProfileService.GetSelectedUserProfiles(users.Select(x => x.UserId).ToList());
							cqi.DepartmentTextNumber = _departmentSettingsService.GetTextToCallNumberForDepartment(cqi.Call.DepartmentId);

							_queueService.EnqueueCallBroadcast(cqi);

							messageEvent.Processed = true;
						}

						if (!isDispatchSource && textCommandEnabled)
						{
							if (profile != null)
							{
								var payload = _textCommandService.DetermineType(textMessage.Text);
								var customActions = customStates.FirstOrDefault(x => x.Type == (int)CustomStateTypes.Personnel);
								var customStaffing = customStates.FirstOrDefault(x => x.Type == (int)CustomStateTypes.Staffing);

								switch (payload.Type)
								{
									case TextCommandTypes.None:
										_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", "Resgrid (https://resgrid.com) Automated Text System. Unknown command, text help for supported commands.", department.DepartmentId, textMessage.To, profile);
										break;
									case TextCommandTypes.Help:
										messageEvent.Processed = true;

										var help = new StringBuilder();
										help.Append("Resgrid Text Commands" + Environment.NewLine);
										help.Append("---------------------" + Environment.NewLine);
										help.Append("These are the commands you can text to alter your status and staffing. Text help for help." + Environment.NewLine);
										help.Append("---------------------" + Environment.NewLine);
										help.Append("Core Commands" + Environment.NewLine);
										help.Append("---------------------" + Environment.NewLine);
										help.Append("STOP: To turn off all text messages" + Environment.NewLine);
										help.Append("HELP: This help text" + Environment.NewLine);
										help.Append("CALLS: List active calls" + Environment.NewLine);
										help.Append("C[CallId]: Get Call Detail i.e. C1445" + Environment.NewLine);
										help.Append("UNITS: List unit statuses" + Environment.NewLine);
										help.Append("---------------------" + Environment.NewLine);
										help.Append("Status or Action Commands" + Environment.NewLine);
										help.Append("---------------------" + Environment.NewLine);

										if (customActions != null && customActions.IsDeleted == false && customActions.GetActiveDetails() != null && customActions.GetActiveDetails().Any())
										{
											var activeDetails = customActions.GetActiveDetails();
											for (int i = 0; i < activeDetails.Count; i++)
											{
												help.Append($"{activeDetails[i].ButtonText.Trim().Replace(" ", "").Replace("-", "").Replace(":", "")} or {i + 1}: {activeDetails[i].ButtonText}" + Environment.NewLine);
											}
										}
										else
										{
											help.Append("responding or 1: Responding" + Environment.NewLine);
											help.Append("notresponding or 2: Not Responding" + Environment.NewLine);
											help.Append("onscene or 3: On Scene" + Environment.NewLine);
											help.Append("available or 4: Available" + Environment.NewLine);
										}
										help.Append("---------------------" + Environment.NewLine);
										help.Append("Staffing Commands" + Environment.NewLine);
										help.Append("---------------------" + Environment.NewLine);

										if (customStaffing != null && customStaffing.IsDeleted == false && customStaffing.GetActiveDetails() != null && customStaffing.GetActiveDetails().Any())
										{
											var activeDetails = customStaffing.GetActiveDetails();
											for (int i = 0; i < activeDetails.Count; i++)
											{
												help.Append($"{activeDetails[i].ButtonText.Trim().Replace(" ", "").Replace("-", "").Replace(":", "")} or S{i + 1}: {activeDetails[i].ButtonText}" + Environment.NewLine);
											}
										}
										else
										{
											help.Append("available or s1: Available Staffing" + Environment.NewLine);
											help.Append("delayed or s2: Delayed Staffing" + Environment.NewLine);
											help.Append("unavailable or s3: Unavailable Staffing" + Environment.NewLine);
											help.Append("committed or s4: Committed Staffing" + Environment.NewLine);
											help.Append("onshift or s4: On Shift Staffing" + Environment.NewLine);
										}

										_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", help.ToString(), department.DepartmentId, textMessage.To, profile);
										break;
									case TextCommandTypes.Action:
										messageEvent.Processed = true;
										_actionLogsService.SetUserAction(profile.UserId, department.DepartmentId, (int)payload.GetActionType());
										_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", string.Format("Resgrid received your text command. Status changed to: {0}", payload.GetActionType()), department.DepartmentId, textMessage.To, profile);
										break;
									case TextCommandTypes.Staffing:
										messageEvent.Processed = true;
										_userStateService.CreateUserState(profile.UserId, department.DepartmentId, (int)payload.GetStaffingType());
										_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", string.Format("Resgrid received your text command. Staffing level changed to: {0}", payload.GetStaffingType()), department.DepartmentId, textMessage.To, profile);
										break;
									case TextCommandTypes.Stop:
										messageEvent.Processed = true;
										_userProfileService.DisableTextMessagesForUser(profile.UserId);
										_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", "Text messages are now turned off for this user, to enable again log in to Resgrid and update your profile.", department.DepartmentId, textMessage.To, profile);
										break;
									case TextCommandTypes.CustomAction:
										messageEvent.Processed = true;
										_actionLogsService.SetUserAction(profile.UserId, department.DepartmentId, payload.GetCustomActionType());

										if (customActions != null && customActions.IsDeleted == false && customActions.GetActiveDetails() != null && customActions.GetActiveDetails().Any() && customActions.GetActiveDetails().FirstOrDefault(x => x.CustomStateDetailId == payload.GetCustomActionType()) != null)
										{
											var detail = customActions.GetActiveDetails().FirstOrDefault(x => x.CustomStateDetailId == payload.GetCustomActionType());
											_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", string.Format("Resgrid received your text command. Status changed to: {0}", detail.ButtonText), department.DepartmentId, textMessage.To, profile);
										}
										else
										{
											_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", "Resgrid received your text command and updated your status", department.DepartmentId, textMessage.To, profile);
										}
										break;
									case TextCommandTypes.CustomStaffing:
										messageEvent.Processed = true;
										_userStateService.CreateUserState(profile.UserId, department.DepartmentId, payload.GetCustomStaffingType());

										if (customStaffing != null && customStaffing.IsDeleted == false && customStaffing.GetActiveDetails() != null && customStaffing.GetActiveDetails().Any() && customStaffing.GetActiveDetails().FirstOrDefault(x => x.CustomStateDetailId == payload.GetCustomStaffingType()) != null)
										{
											var detail = customStaffing.GetActiveDetails().FirstOrDefault(x => x.CustomStateDetailId == payload.GetCustomStaffingType());
											_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", string.Format("Resgrid received your text command. Staffing changed to: {0}", detail.ButtonText), department.DepartmentId, textMessage.To, profile);
										}
										else
										{
											_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", "Resgrid received your text command and updated your staffing", department.DepartmentId, textMessage.To, profile);
										}
										break;
									case TextCommandTypes.MyStatus:
										messageEvent.Processed = true;


										var userStatus = _actionLogsService.GetLastActionLogForUser(profile.UserId);
										var userStaffing = _userStateService.GetLastUserStateByUserId(profile.UserId);

										var customStatusLevel = _customStateService.GetCustomPersonnelStatus(department.DepartmentId, userStatus);
										var customStaffingLevel = _customStateService.GetCustomPersonnelStaffing(department.DepartmentId, userStaffing);

										_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", $"Hello {profile.FullName.AsFirstNameLastName} at {DateTime.UtcNow.TimeConverterToString(department)} your current status is {customStatusLevel.ButtonText} and your current staffing is {customStaffingLevel.ButtonText}.", department.DepartmentId, textMessage.To, profile);
										break;
									case TextCommandTypes.Calls:
										messageEvent.Processed = true;

										var activeCalls = _callsService.GetActiveCallsByDepartment(department.DepartmentId);

										var activeCallText = new StringBuilder();
										activeCallText.Append($"Active Calls for {department.Name}" + Environment.NewLine);
										activeCallText.Append("---------------------" + Environment.NewLine);

										foreach (var activeCall in activeCalls)
										{
											activeCallText.Append($"CallId: {activeCall.CallId} Name: {activeCall.Name} Nature:{activeCall.NatureOfCall}" + Environment.NewLine);
										}


										_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", activeCallText.ToString(), department.DepartmentId, textMessage.To, profile);
										break;
									case TextCommandTypes.Units:
										messageEvent.Processed = true;

										var unitStatus = _unitsService.GetAllLatestStatusForUnitsByDepartmentId(department.DepartmentId);

										var unitStatusesText = new StringBuilder();
										unitStatusesText.Append($"Unit Statuses for {department.Name}" + Environment.NewLine);
										unitStatusesText.Append("---------------------" + Environment.NewLine);

										foreach (var unit in unitStatus)
										{
											var unitState = _customStateService.GetCustomUnitState(unit);
											unitStatusesText.Append($"{unit.Unit.Name} is {unitState.ButtonText}" + Environment.NewLine);
										}

										_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", unitStatusesText.ToString(), department.DepartmentId, textMessage.To, profile);
										break;
									case TextCommandTypes.CallDetail:
										messageEvent.Processed = true;

										var call = _callsService.GetCallById(int.Parse(payload.Data));

										var callText = new StringBuilder();
										callText.Append($"Call Information for {call.Name}" + Environment.NewLine);
										callText.Append("---------------------" + Environment.NewLine);
										callText.Append($"Id: {call.CallId}" + Environment.NewLine);
										callText.Append($"Number: {call.Number}" + Environment.NewLine);
										callText.Append($"Logged: {call.LoggedOn.TimeConverterToString(department)}" + Environment.NewLine);
										callText.Append("-----Nature-----" + Environment.NewLine);
										callText.Append(call.NatureOfCall + Environment.NewLine);
										callText.Append("-----Address-----" + Environment.NewLine);

										if (!String.IsNullOrWhiteSpace(call.Address))
											callText.Append(call.Address + Environment.NewLine);
										else if (!string.IsNullOrEmpty(call.GeoLocationData))
										{
											try
											{
												string[] points = call.GeoLocationData.Split(char.Parse(","));

												if (points != null && points.Length == 2)
												{
													callText.Append(_geoLocationProvider.GetAproxAddressFromLatLong(double.Parse(points[0]), double.Parse(points[1])) + Environment.NewLine);
												}
											}
											catch
											{
											}
										}

										_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", callText.ToString(), department.DepartmentId, textMessage.To, profile);
										break;
								}
							}
						}
					}
					else if (textMessage.To == Config.NumberProviderConfig.SignalWireResgridNumber.Replace("+", "")) // Resgrid master text number
					{
						var payload = _textCommandService.DetermineType(textMessage.Text);

						switch (payload.Type)
						{
							case TextCommandTypes.None:
								_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", "Resgrid (https://resgrid.com) Automated Text System. Unknown command, text help for supported commands.", department.DepartmentId, textMessage.To, profile);
								break;
							case TextCommandTypes.Help:
								messageEvent.Processed = true;

								var help = new StringBuilder();
								help.Append("Resgrid Text Commands" + Environment.NewLine);
								help.Append("---------------------" + Environment.NewLine);
								help.Append("This is the Resgrid system for first responders (https://resgrid.com) automated text system. Your department isn't signed up for inbound text messages, but you can send the following commands." + Environment.NewLine);
								help.Append("---------------------" + Environment.NewLine);
								help.Append("STOP: To turn off all text messages" + Environment.NewLine);
								help.Append("HELP: This help text" + Environment.NewLine);

								_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", help.ToString(), department.DepartmentId, textMessage.To, profile);

								break;
							case TextCommandTypes.Stop:
								messageEvent.Processed = true;
								_userProfileService.DisableTextMessagesForUser(profile.UserId);
								_communicationService.SendTextMessage(profile.UserId, "Resgrid TCI Help", "Text messages are now turned off for this user, to enable again log in to Resgrid and update your profile.", department.DepartmentId, textMessage.To, profile);
								break;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Framework.Logging.LogException(ex);
			}
			finally
			{
				_numbersService.SaveInboundMessageEvent(messageEvent);
			}


			return new HttpResponseMessage(HttpStatusCode.OK);
		}
	}
}