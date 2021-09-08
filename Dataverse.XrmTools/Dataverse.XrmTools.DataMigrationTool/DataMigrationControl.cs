﻿// System
using System;
using System.IO;
using System.Xml;
using System.Data;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Specialized;

// Microsoft
using Microsoft.Xrm.Sdk;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Tooling.Connector;

// 3rd Party
using McTools.Xrm.Connection;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Args;
using XrmToolBox.Extensibility.Interfaces;

// DataMigrationTool
using Dataverse.XrmTools.DataMigrationTool.Enums;
using Dataverse.XrmTools.DataMigrationTool.Logic;
using Dataverse.XrmTools.DataMigrationTool.Models;
using Dataverse.XrmTools.DataMigrationTool.Helpers;
using Dataverse.XrmTools.DataMigrationTool.AppSettings;
using Dataverse.XrmTools.DataMigrationTool.Repositories;
using Dataverse.XrmTools.DataMigrationTool.Forms;

namespace Dataverse.XrmTools.DataMigrationTool
{
    public partial class DataMigrationControl : MultipleConnectionsPluginControlBase, IStatusBarMessenger, IMessageBusHost
    {
        #region Variables
        // settings
        private Settings _settings;

        // service
        private CrmServiceClient _sourceClient;
        private CrmServiceClient _targetClient;

        // main objects
        private Instance _instance;
        private IEnumerable<Table> _tables;
        private IEnumerable<Sort> _sorts;

        // flags
        private bool _working;
        #endregion Variables

        #region Handlers
        public event EventHandler<StatusBarMessageEventArgs> SendMessageToStatusBar;
        public event EventHandler<MessageBusEventArgs> OnOutgoingMessage;
        #endregion Variables

        public DataMigrationControl()
        {
            LogInfo("----- Starting Data Migration Tool -----");

            LogInfo("Loading Settings...");
            SettingsHelper.GetSettings(out _settings);

            LogInfo("Initializing components...");
            InitializeComponent();
        }

        public void DataMigrationControl_Load(object sender, EventArgs e)
        {
            ExecuteMethod(WhoAmI);
        }

        #region Interface Methods
        /// <summary>
        /// This event occurs when the connection has been updated in XrmToolBox
        /// </summary>
        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            try
            {
                LogInfo($"Updating connection: {actionName}...");
                base.UpdateConnection(newService, detail, actionName, parameter);
                _sourceClient = Service as CrmServiceClient;

                if (actionName != "AdditionalOrganization")
                {
                    var orgId = detail.ConnectionId.Value;
                    _instance = _settings.Instances.FirstOrDefault(org => org.Id.Equals(orgId));
                    if (_instance == null)
                    {
                        _instance = new Instance
                        {
                            Id = orgId,
                            Name = detail.ConnectionName,
                            Mappings = new List<Mapping>()
                        };

                        _settings.Instances.Add(_instance);
                    }

                    // load UI settings
                    _sorts = _settings.Sorts;

                    // render UI components
                    LogInfo($"Rendering UI components...");
                    RenderConnectionLabel(ConnectionType.Source, _instance.Name);
                    RenderMappingsButton();
                    txtExportDirPath.Text = _settings.ExportPath;
                    EnableComponentsOnMainConnection();

                    // save settings file
                    SettingsHelper.SetSettings(_settings);

                    // load tables when source connection changes
                    LoadTables();
                }
            }
            catch (Exception ex)
            {
                LogError(ex.Message);
                MessageBox.Show(this, $"An error occurred: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void ConnectionDetailsUpdated(NotifyCollectionChangedEventArgs args)
        {
            try
            {
                if (args.Action.Equals(NotifyCollectionChangedAction.Add))
                {
                    var detail = (ConnectionDetail)args.NewItems[0];
                    _targetClient = detail.ServiceClient;

                    if (_sourceClient == null) { throw new Exception("Source connection is invalid"); }
                    LogInfo($"Source OrgId: {_sourceClient.ConnectedOrgId}");
                    LogInfo($"Source OrgName: {_sourceClient.ConnectedOrgUniqueName}");
                    LogInfo($"Source EnvId: {_sourceClient.EnvironmentId}");

                    if (_targetClient == null) { throw new Exception("Target connection is invalid"); }
                    LogInfo($"Target OrgId: {_targetClient.ConnectedOrgId}");
                    LogInfo($"Target OrgName: {_targetClient.ConnectedOrgUniqueName}");
                    LogInfo($"Target EnvId: {_targetClient.EnvironmentId}");

                    if (_sourceClient.ConnectedOrgId.Equals(_targetClient.ConnectedOrgId) && _sourceClient.EnvironmentId.Equals(_targetClient.EnvironmentId))
                    {
                        throw new Exception("Source and Target connections must refer to different Dataverse instances");
                    }

                    RenderConnectionLabel(ConnectionType.Target, detail.ConnectionName);
                    EnableComponentsOnSecondaryConnection();

                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Target Connection ready"));
                }
            }
            catch (Exception ex)
            {
                LogError(ex.Message);
                MessageBox.Show(this, $"An error occurred: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void OnIncomingMessage(MessageBusEventArgs message)
        {
            try
            {
                if (message.SourcePlugin.Equals("FetchXML Builder") && message.TargetArgument is string fetchXml && !string.IsNullOrWhiteSpace(fetchXml))
                {
                    var filters = ExtractFilterNode(fetchXml);

                    var tableData = GetSelectedTableItemData(false);
                    if (tableData == null)
                    {
                        SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Error setting filters from FetchXML Builder"));
                        return;
                    }

                    // save settings
                    tableData.Settings.Filter = filters;
                    SettingsHelper.SetSettings(_settings);

                    // set text box
                    rtbFilter.Text = filters;
                }
            }
            catch (Exception ex)
            {
                LogError(ex.Message);
                MessageBox.Show(this, $"An error occurred: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        #endregion Interface Methods

        #region Private Main Methods
        private void WhoAmI()
        {
            Service.Execute(new WhoAmIRequest());
        }

        private void LoadTables()
        {
            LogInfo($"Loading tables...");

            if (Service == null)
            {
                ExecuteMethod(WhoAmI);
                return;
            }

            if (_working) { return; }

            lvTables.Items.Clear();
            lvAttributes.Items.Clear();

            ManageWorkingState(true);

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading tables...",
                Work = (worker, args) =>
                {
                    var repo = new CrmRepo(Service, worker);
                    var tableMetadata = repo.GetOrgTables();

                    args.Result = tableMetadata.Select(tbl => new Table
                    {
                        LogicalName = tbl.LogicalName,
                        DisplayName = tbl.DisplayName.UserLocalizedLabel != null ? tbl.DisplayName.UserLocalizedLabel.Label : string.Empty,
                        IdAttribute = tbl.PrimaryIdAttribute,
                        NameAttribute = tbl.PrimaryNameAttribute,
                        IsCustomizable = tbl.IsCustomizable.Value
                    }).ToList();
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(this, $"An error occurred: {args.Error.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    else
                    {
                        // load tables list
                        _tables = args.Result as IEnumerable<Table>;
                        LoadTablesList();

                        SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Tables load complete"));
                    }
                }
            });
        }

        private void LoadTablesList()
        {
            LogInfo($"Rendering tables list view...");

            var textFilter = txtTableFilter.Text;
            var filtered = _tables.Where(tbl => string.IsNullOrWhiteSpace(textFilter) || tbl.MatchFilter(textFilter));

            foreach (var tbl in filtered)
            {
                var item = tbl.ToListViewItem();
                if (!tbl.IsCustomizable)
                {
                    item.ForeColor = Color.Gray;
                    item.SubItems.Add("Not customizable");
                }

                lvTables.Items.Add(item);
            }

            // re-render list view columns
            var maxWidth = lvTables.Width >= 150 ? lvTables.Width * 2 : 300;
            chTblDisplayName.Width = (int)Math.Floor(maxWidth * 0.50);
            chTblLogicalName.Width = (int)Math.Floor(maxWidth * 0.50);

            ManageWorkingState(false);
        }

        private void LoadAttributes()
        {
            LogInfo($"Loading attributes...");

            if (_working) { return; }

            lvAttributes.Items.Clear();

            ManageWorkingState(true);

            var tableData = GetSelectedTableItemData(false);
            if (tableData == null)
            {
                SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Error loading attributes"));
                return;
            }

            WorkAsync(new WorkAsyncInfo
            {
                Message = $"Loading {tableData.Table.DisplayName} attributes...",
                Work = (worker, args) =>
                {
                    cbSelectAll.Checked = false;

                    // filter valid attributes
                    args.Result = tableData.Metadata.Attributes
                        .Where(att => att.IsValidForRead != null && att.IsValidForRead.Value)
                        .Where(att => att.IsValidForCreate != null && att.IsValidForCreate.Value)
                        .Where(att => att.DisplayName != null && att.DisplayName.UserLocalizedLabel != null && !string.IsNullOrEmpty(att.DisplayName.UserLocalizedLabel.Label))
                        .Select(att => new Models.Attribute
                        {
                            Type = att.AttributeTypeName.Value.EndsWith("Type") ? att.AttributeTypeName.Value.Substring(0, att.AttributeTypeName.Value.LastIndexOf("Type")) : att.AttributeTypeName.Value,
                            LogicalName = att.LogicalName,
                            DisplayName = att.DisplayName.UserLocalizedLabel.Label,
                            Updatable = att.IsValidForUpdate.Value
                        })
                        .ToList();
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        gbAttributes.Visible = false;
                        gbFilters.Visible = false;
                        MessageBox.Show(this, $"An error occurred: {args.Error.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    else
                    {
                        // save settings
                        tableData.Settings.DisplayName = tableData.Table.DisplayName;
                        tableData.Settings.IsCustomizable = tableData.Table.IsCustomizable;
                        SettingsHelper.SetSettings(_settings);

                        // load attributes
                        tableData.Table.AllAttributes = args.Result as List<Models.Attribute>;
                        LoadAttributesList(tableData);

                        SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Attributes load complete"));
                    }
                }
            });
        }

        private void LoadAttributesList(TableData tableData)
        {
            LogInfo($"Rendering attributes list view...");

            var deselected = tableData.Settings.DeselectedAttributes;
            if (deselected == null)
            {
                deselected = new List<string>();
                deselected.AddRange(_instance.DefaultDeselected);

                // save settings
                tableData.Settings.DeselectedAttributes = deselected;
                SettingsHelper.SetSettings(_settings);
            }

            foreach (var att in tableData.Table.AllAttributes)
            {
                var item = att.ToListViewItem();
                if (!att.Updatable)
                {
                    item.ForeColor = Color.Gray;
                    item.SubItems.Add("Read-only attribute");
                }

                item.Checked = !deselected.Any(dsl => dsl.Equals(att.LogicalName));

                lvAttributes.Items.Add(item);
            }

            // re-render list view columns
            var maxWidth = lvAttributes.Width >= 500 ? lvAttributes.Width : 500;
            chAttrDisplayName.Width = (int)Math.Floor(maxWidth * 0.25);
            chAttrLogicalName.Width = (int)Math.Floor(maxWidth * 0.25);
            chAttrType.Width = (int)Math.Floor(maxWidth * 0.20);
            chAttrDescription.Width = (int)Math.Floor(maxWidth * 0.30);

            gbAttributes.Visible = true;

            ManageWorkingState(false);
        }

        private void LoadFilters(TableData tableData)
        {
            LogInfo($"Loading table filters...");

            rtbFilter.Text = string.Empty;

            tableData = tableData != null ? tableData : GetSelectedTableItemData(false);
            if (tableData == null || tableData.Settings == null)
            {
                SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Error loading filters"));
                return;
            }

            rtbFilter.Text = tableData.Settings.Filter;

            gbFilters.Visible = true;
        }

        private void PreviewData()
        {
            LogInfo($"Previewing operation...");

            var tableData = GetSelectedTableItemData(attributeRequired: true);
            if(tableData == null) {
                SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Preview operation aborted"));
                return;
            }

            if (cbDelete.Checked)
            {
                if (string.IsNullOrWhiteSpace(tableData.Settings.Filter))
                {
                    throw new Exception("Delete operation whithout any filter applied is not supported. You must add a filter in order to perform a Delete operation");
                }
                else
                {
                    var msg = $"WARNING: Delete operation\nAll '{tableData.Table.LogicalName}' records on target instance that doesn't match the filter will be deleted!\n\nContinue?";
                    var result = MessageBox.Show(msg, "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (result.Equals(DialogResult.No)) { return; }
                }
            }

            ManageWorkingState(true);

            var uiSettings = ReadSettings(Enums.Action.Preview);

            tableData.SelectedAttributes = lvAttributes.CheckedItems
                .Cast<ListViewItem>()
                .Select(lvi => lvi.ToObject(new Models.Attribute()) as Models.Attribute)
                .Select(attr => tableData.Table.AllAttributes.FirstOrDefault(tblAttr => tblAttr.LogicalName.Equals(attr.LogicalName)))
                .ToList();

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Previewing operation...",
                AsyncArgument = tableData,
                IsCancelable = true,
                Work = (worker, evt) =>
                {
                    var data = evt.Argument as TableData;

                    // check mappings
                    var mappings = GetMappings(uiSettings);

                    var logic = new DataLogic(worker, Service, _targetClient);
                    var result = Task.Run(() => logic.Preview(data, uiSettings));

                    evt.Result = result.Result;
                },
                PostWorkCallBack = evt =>
                {
                    if (evt.Result != null)
                    {
                        // show preview form
                        var result = evt.Result as OperationResult;

                        var prvwDialog = new Results(result.Items, _settings);
                        prvwDialog.ShowDialog(ParentForm);

                        SettingsHelper.SetSettings(_settings);
                    }

                    tsbAbort.Text = "Abort";
                    ManageWorkingState(false);
                },
                ProgressChanged = evt =>
                {
                    SetWorkingMessage(evt.UserState.ToString());
                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs(evt.ProgressPercentage * 100, evt.UserState.ToString()));
                }
            });
        }

        private void Export()
        {
            LogInfo($"Export operation...");

            var tableData = GetSelectedTableItemData(false, true);
            if (tableData == null)
            {
                SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Export operation aborted"));
                return;
            }

            ManageWorkingState(true);

            var uiSettings = ReadSettings(Enums.Action.None);

            tableData.SelectedAttributes = lvAttributes.CheckedItems
                .Cast<ListViewItem>()
                .Select(lvi => lvi.ToObject(new Models.Attribute()) as Models.Attribute)
                .Select(attr => tableData.Table.AllAttributes.FirstOrDefault(tblAttr => tblAttr.LogicalName.Equals(attr.LogicalName)))
                .ToList();

            var path = txtExportDirPath.Text;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Exporting records...",
                AsyncArgument = tableData,
                IsCancelable = true,
                Work = (worker, evt) =>
                {
                    var data = evt.Argument as TableData;

                    var logic = new DataLogic(worker, Service, _targetClient);
                    logic.Export(data, uiSettings, path);
                },
                PostWorkCallBack = evt =>
                {
                    tsbAbort.Text = "Abort";
                    ManageWorkingState(false);
                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Export complete"));
                }
            });
        }

        private void Import()
        {
            LogInfo($"Import operation...");

            var tableData = GetSelectedTableItemData(attributeRequired: true);
            if (tableData == null)
            {
                SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Import operation aborted"));
                return;
            }

            ManageWorkingState(true);

            var uiSettings = ReadSettings(Enums.Action.None);

            var dialog = new OpenFileDialog
            {
                FileName = $"{tableData.Table.LogicalName}.json",
                Filter = "Json files (*.json)|*.json",
                FilterIndex = 2,
                RestoreDirectory = true
            };

            if (!string.IsNullOrWhiteSpace(txtExportDirPath.Text))
            {
                dialog.InitialDirectory = txtExportDirPath.Text;
            }

            var path = GetJsonFilePath(dialog);

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Importing records...",
                AsyncArgument = tableData,
                IsCancelable = true,
                Work = (worker, evt) =>
                {
                    var data = evt.Argument as TableData;

                    var logic = new DataLogic(worker, Service, _targetClient);

                    var result = Task.Run(() => logic.Import(data, path, uiSettings));

                    evt.Result = result.Result;
                },
                PostWorkCallBack = evt =>
                {
                    if (evt.Result != null)
                    {
                        // show results form
                        var result = evt.Result as OperationResult;

                        var resDialog = new Results(result.Items, _settings);
                        resDialog.ShowDialog(ParentForm);

                        SettingsHelper.SetSettings(_settings);
                    }

                    tsbAbort.Text = "Abort";
                    ManageWorkingState(false);
                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Import complete"));
                }
            });
        }

        private string GetJsonFilePath(FileDialog dialog)
        {
            var path = string.Empty;
            using (var ofd = dialog as OpenFileDialog)
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    path = ofd.FileName;
                }
            }

            return path;
        }

        private void ImportExportTableSettings(ImportExportAction action, FileDialog dialog, TableData tableData)
        {
            if (action.Equals(ImportExportAction.Export))
            {
                using (var sfd = dialog as SaveFileDialog)
                {
                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        // save serialized json with settings
                        var path = sfd.FileName;
                        var json = tableData.Settings.SerializeObject<TableSettings>();
                        File.WriteAllText(path, json);

                        SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs($"Successfully exported settings for table '{tableData.Table.DisplayName}'"));
                    }
                }
            }
            else if (action.Equals(ImportExportAction.Import))
            {
                using (var ofd = dialog as OpenFileDialog)
                {
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        // get deserialized settings object from json
                        var path = ofd.FileName;
                        var json = File.ReadAllText(path);

                        var deserialized = json.DeserializeObject<TableSettings>();

                        // re-set settings from json file
                        tableData.Settings.Filter = deserialized.Filter;
                        tableData.Settings.DeselectedAttributes = deserialized.DeselectedAttributes;

                        LoadAttributes();
                        LoadFilters(tableData);

                        SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs($"Successfully imported settings for table '{tableData.Table.DisplayName}'"));
                    }
                }
            }
        }

        private List<Mapping> GetMappings(UiSettings ui)
        {
            LogInfo($"Parsing automatic mappings...");

            var mappings = new List<Mapping>(_instance.Mappings);
            var mappingsLogic = new MappingsLogic(Service, _targetClient);

            if (ui.MapUsers)
            {
                SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Mapping Users..."));

                var usrMappings = mappingsLogic.GetUserMappings();
                if (usrMappings.Any()) { mappings.AddRange(usrMappings); }
            }
            if (ui.MapTeams)
            {
                SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Mapping Teams..."));

                var teamMappings = mappingsLogic.GetTeamMappings();
                if (teamMappings.Any()) { mappings.AddRange(teamMappings); }
            }
            if (ui.MapBu)
            {
                SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Mapping root Business Unit..."));

                var buMapping = mappingsLogic.GetBusinessUnitMapping();
                if (buMapping != null) { mappings.Add(buMapping); }
            }

            return mappings;
        }

        private TableData GetSelectedTableItemData(bool targetRequired = true, bool attributeRequired = false)
        {
            LogInfo($"Parsing table data...");

            if (Service == null || (targetRequired && _targetClient == null))
            {
                throw new Exception("You must select both a source and a target organization");
            }
            if (targetRequired && !(cbCreate.Checked || cbUpdate.Checked || cbDelete.Checked))
            {
                throw new Exception("You must select at least one setting for transporting the data");
            }
            if (lvTables.SelectedItems.Count == 0)
            {
                throw new Exception("You must select a table first");
            }
            if (attributeRequired && lvAttributes.CheckedItems.Count == 0)
            {
                throw new Exception("At least one attribute must be selected");
            }

            var tableItem = lvTables.SelectedItems[0].ToObject(new Table()) as Table;
            if (tableItem == null || string.IsNullOrEmpty(tableItem.DisplayName) || string.IsNullOrEmpty(tableItem.LogicalName) || !_tables.Any(tbl => tbl.LogicalName.Equals(tableItem.LogicalName)))
            {
                throw new Exception("Invalid Table: Please reload tables and try again");
            }

            var table = _tables.FirstOrDefault(tbl => tbl.LogicalName.Equals(tableItem.LogicalName));

            var repo = new CrmRepo(Service);
            var metadata = repo.GetTableMetadata(table.LogicalName);

            var tableSettings = _settings.GetTableSettings(_tables, table.LogicalName);
            if (tableSettings == null)
            {
                throw new Exception("Invalid Table: Please reload tables and try again");
            }

            return new TableData { Table = table, Settings = tableSettings, Metadata = metadata };
        }
        #endregion Private Main Methods

        #region Private Helper Methods
        private void ManageWorkingState(bool working)
        {
            _working = working;
            Cursor = working ? Cursors.WaitCursor : Cursors.Default;
            tsbAbort.Visible = working;
        }

        private void RenderConnectionLabel(ConnectionType serviceType, string name)
        {
            var label = serviceType.Equals(ConnectionType.Source) ? lblSourceValue : lblTargetValue;
            label.Text = name;
            label.ForeColor = Color.MediumSeaGreen;
        }

        private void RenderMappingsButton()
        {
            btnMappings.Font = _instance.Mappings.Any() ? new Font(btnMappings.Font.Name, btnMappings.Font.Size, FontStyle.Bold) : new Font(btnMappings.Font.Name, btnMappings.Font.Size, FontStyle.Regular);
        }

        private void EnableComponentsOnMainConnection()
        {
            btnSelectTarget.Enabled = true;
            gbImportExport.Enabled = true;
            gbOrgSettings.Enabled = true;
            gbOpSettings.Enabled = true;
            gbTables.Enabled = true;
        }

        private void EnableComponentsOnSecondaryConnection()
        {
            tsbPreview.Enabled = true;
            tsbExport.Enabled = true;
            tsbImport.Enabled = true;
        }

        public UiSettings ReadSettings(Enums.Action initial)
        {
            var mode = initial;
            if (cbCreate.Checked) mode |= Enums.Action.Create;
            if (cbUpdate.Checked) mode |= Enums.Action.Update;
            if (cbDelete.Checked) mode |= Enums.Action.Delete;

            return new UiSettings
            {
                Action = mode,
                BatchSize= nudBatchCount.Value.ToInt().Value,
                MapUsers = cbMapUsers.Checked,
                MapTeams = cbMapTeams.Checked,
                MapBu = cbMapBu.Checked
            };
        }

        private string ExtractFilterNode(string fetchXml)
        {
            var doc = new XmlDocument();

            var filters = string.Empty;

            if(!string.IsNullOrWhiteSpace(fetchXml))
            {
                // load xml
                doc.LoadXml(fetchXml);

                var filterNodes = doc.SelectNodes("/fetch/entity/filter");
                if (filterNodes.Count > 0)
                {
                    filters = filterNodes[0].OuterXml;
                }
            }

            return filters.FormatXml();
        }

        private string ParseFetchQuery(string filters)
        {
            var tableData = GetSelectedTableItemData(false);
            if (tableData == null)
            {
                throw new Exception("Error parsing query to FetchXML Builder");
            }

            // create new document
            var newDoc = new XmlDocument();

            // fetch node (root)
            var root = newDoc.CreateElement("fetch");
            newDoc.AppendChild(root);

            // entity node
            var entity = newDoc.CreateElement("entity");

            // entity node attributes
            var entityNameAttr = newDoc.CreateAttribute("name");
            entityNameAttr.Value = tableData.Table.LogicalName;
            entity.Attributes.Append(entityNameAttr);

            // append to root
            root.AppendChild(entity);

            if(!string.IsNullOrWhiteSpace(filters))
            {
                // load document from filters xml
                var filtersDoc = new XmlDocument();
                filtersDoc.LoadXml(filters);

                // get node
                var filtersNode = filtersDoc.FirstChild;

                // need to import node to newDoc document before append
                var importNode = newDoc.ImportNode(filtersNode, true);

                // append to new document
                entity.AppendChild(importNode);
            }

            return newDoc.OuterXml;
        }
        #endregion Private Helper Methods

        #region Form events
        private async void txtTableFilter_TextChanged(object sender, EventArgs e)
        {
            async Task<bool> UserKeepsTyping()
            {
                var txt = txtTableFilter.Text;
                await Task.Delay(500);

                return txt != txtTableFilter.Text;
            }

            if (await UserKeepsTyping()) return;

            // user is done typing -> execute logic
            try
            {
                lvTables.Items.Clear();
                ManageWorkingState(true);

                LoadTablesList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                ManageWorkingState(false);
            }
        }

        private void lvTables_SelectedIndexChanged(object sender, EventArgs e)
        {
            try
            {
                if (lvTables.SelectedItems.Count > 0)
                {
                    LoadAttributes();
                    LoadFilters(null);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void listView_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            try
            {
                (sender as ListView).Sort(_settings, e.Column);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnSelectTarget_Click(object sender, EventArgs e)
        {
            try
            {
                AddAdditionalOrganization();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void tsbRefreshTables_Click(object sender, EventArgs e)
        {
            try
            {
                LoadTables();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void tsbPreview_Click(object sender, EventArgs e)
        {
            try
            {
                PreviewData();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void tsbExport_Click(object sender, EventArgs e)
        {
            try
            {
                Export();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void tsbImport_Click(object sender, EventArgs e)
        {
            try
            {
                Import();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void tsbAbort_Click(object sender, EventArgs e)
        {
            try
            {
                CancelWorker();
                tsbAbort.Text = "Aborting operation...";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void cbAllAttributes_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (lvAttributes.Items.Count == 0) { return; }

                var allAttributes = (sender as CheckBox).Checked;

                var tableData = GetSelectedTableItemData(false);
                if (tableData == null)
                {
                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Error saving selected attributes"));
                    return;
                }

                // check/uncheck all attributes
                lvAttributes.Items.Cast<ListViewItem>().ToList().ForEach(item => item.Checked = allAttributes);

                // save settings
                if (allAttributes) { tableData.Settings.DeselectedAttributes.Clear(); }
                else { tableData.Settings.DeselectedAttributes = tableData.Table.AllAttributes.Select(attr => attr.LogicalName).ToList(); }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void lvAttributes_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            try
            {
                var listView = sender as ListView;
                if (listView.FocusedItem != null && lvTables.SelectedItems.Count > 0)
                {
                    var tableData = GetSelectedTableItemData(false);
                    if (tableData == null)
                    {
                        SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Error saving selected attributes"));
                        return;
                    }

                    // save deselected attributes to settings
                    var deselected = lvAttributes.Items.Cast<ListViewItem>().ToList().Where(lvi => !lvi.Checked).Select(lvi => lvi.SubItems[1].Text);

                    tableData.Settings.DeselectedAttributes.Clear();
                    tableData.Settings.DeselectedAttributes.AddRange(deselected);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnExportTableSettings_Click(object sender, EventArgs e)
        {
            try
            {
                var tableData = GetSelectedTableItemData(false);
                if (tableData == null)
                {
                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Error saving settings"));
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    FileName = $"{tableData.Table.LogicalName}.settings.json",
                    Filter = "Json files (*.json)|*.json",
                    FilterIndex = 2,
                    RestoreDirectory = true
                };

                if (!string.IsNullOrWhiteSpace(txtExportDirPath.Text))
                {
                    dialog.InitialDirectory = txtExportDirPath.Text;
                }

                ImportExportTableSettings(ImportExportAction.Export, dialog, tableData);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnImportTableSettings_Click(object sender, EventArgs e)
        {
            try
            {
                var tableData = GetSelectedTableItemData(false);
                if (tableData == null)
                {
                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Error loading settings"));
                    return;
                }

                var dialog = new OpenFileDialog
                {
                    FileName = $"{tableData.Table.LogicalName}.settings.json",
                    Filter = "Json files (*.json)|*.json",
                    FilterIndex = 2,
                    RestoreDirectory = true
                };

                if (!string.IsNullOrWhiteSpace(txtExportDirPath.Text))
                {
                    dialog.InitialDirectory = txtExportDirPath.Text;
                }

                ImportExportTableSettings(ImportExportAction.Import, dialog, tableData);
                SettingsHelper.SetSettings(_settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnMappings_Click(object sender, EventArgs e)
        {
            if(Service == null) { return; }

            try
            {
                var mappingsDlg = new Mappings(Service, _instance, _tables, _settings);
                mappingsDlg.ShowDialog(ParentForm);

                if (mappingsDlg.Updated)
                {
                    SettingsHelper.SetSettings(_settings);
                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Succesfully updated Organization Mappings"));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnSelectExportDir_Click(object sender, EventArgs e)
        {
            try
            {
                using (var fbd = new FolderBrowserDialog())
                {
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        txtExportDirPath.Text = fbd.SelectedPath;
                        _settings.ExportPath = fbd.SelectedPath;
                        SettingsHelper.SetSettings(_settings);

                        SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Successfully updated Export directory"));
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void txtExportDirPath_Validating(object sender, CancelEventArgs e)
        {
            try
            {
                var txtBox = sender as TextBox;
                var path = txtBox.Text;

                var validDir = Directory.Exists(path);
                if (!validDir)
                {
                    MessageBox.Show("You selected an invalid export directory", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    txtBox.Text = _settings.ExportPath;
                    e.Cancel = true;
                }
                else
                {
                    _settings.ExportPath = path;
                    SettingsHelper.SetSettings(_settings);

                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Successfully updated Export directory"));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnFetchXmlBuilder_Click(object sender, EventArgs e)
        {
            try
            {
                var filters = rtbFilter.Text;
                var fetch = ParseFetchQuery(filters);

                OnOutgoingMessage(this, new MessageBusEventArgs("FetchXML Builder")
                {
                    TargetArgument = fetch
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void rtbFilter_TextChanged(object sender, EventArgs e)
        {
            try
            {
                var filters = rtbFilter.Text;

                var tableData = GetSelectedTableItemData(false);
                if (tableData == null)
                {
                    SendMessageToStatusBar?.Invoke(this, new StatusBarMessageEventArgs("Error saving filters"));
                    return;
                }

                // save settings
                tableData.Settings.Filter = filters;
                SettingsHelper.SetSettings(_settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        #endregion Form events
    }
}