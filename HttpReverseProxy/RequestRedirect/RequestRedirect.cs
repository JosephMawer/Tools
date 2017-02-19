﻿namespace HttpReverseProxy.Plugin.RequestRedirect
{
  using HttpReverseProxyLib.Interface;
  using System.Collections.Generic;
  using System.IO;
  using RequestRedirectConfig = HttpReverseProxy.Plugin.RequestRedirect.Config;


  public partial class RequestRedirect : IPlugin
  {

    #region MEMBERS

    private PluginProperties pluginProperties;
    private Dictionary<string, string> injectHostPathPair;
    private Config pluginConfig = new Config();
    private string configurationFileFullPath;

    #endregion


    #region PROPERTIES

    public PluginProperties PluginProperties { get { return this.pluginProperties; } set { this.pluginProperties = value; } }

    public string ConfigurationFileFullPath { get { return this.configurationFileFullPath; } set { } }

    #endregion


    #region PUBLIC

    /// <summary>
    /// 
    /// </summary>
    public RequestRedirect()
    {
      this.pluginProperties = new PluginProperties()
      {
        Name = RequestRedirectConfig.PluginName,
        Priority = RequestRedirectConfig.PluginPriority,
        Version = RequestRedirectConfig.PluginVersion,
        PluginDirectory = Path.Combine(Directory.GetCurrentDirectory(), "plugins", RequestRedirectConfig.PluginName),
        IsActive = true,
      };

      // Host->Path->type->redirect_resource
      this.injectHostPathPair = new Dictionary<string, string>();
    }

    #endregion


    #region INTERFACE IMPLEMENTATION: Properties    

    public PluginProperties Config { get { return this.pluginProperties; } set { this.pluginProperties = value; } }

    #endregion


    #region INTERFACE IMPLEMENTATION: IComparable

    public int CompareTo(IPlugin other)
    {
      if (other == null)
      {
        return 1;
      }

      if (this.Config.Priority > other.Config.Priority)
      {
        return 1;
      }
      else if (this.Config.Priority < other.Config.Priority)
      {
        return -1;
      }
      else
      {
        return 0;
      }
    }

    #endregion

  }
}

