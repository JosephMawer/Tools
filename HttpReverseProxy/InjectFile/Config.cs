﻿namespace HttpReverseProxy.Plugin.InjectFile
{
  using HttpReverseProxy.Plugin.InjectFile.DataTypes;
  using HttpReverseProxyLib;
  using HttpReverseProxyLib.DataTypes.Enum;
  using HttpReverseProxyLib.Exceptions;
  using System.Collections.Generic;
  using System.IO;
  using System.Text.RegularExpressions;


  public class Config
  {

    #region MEMBERS

    private static string pluginName = "InjectFile";
    private static int pluginPriority = 4;
    private static string pluginVersion = "0.1";
    private static string configFileName = "plugin.config";

    private static List<InjectFileConfigRecord> injectFileRecords = new List<InjectFileConfigRecord>();

    #endregion


    #region PROPERTIES

    public static string PluginName { get { return pluginName; } set { } }

    public static int PluginPriority { get { return pluginPriority; } set { } }

    public static string ConfigFileName { get { return configFileName; } set { } }

    public static string PluginVersion { get { return pluginVersion; } set { } }

    public static List<InjectFileConfigRecord> InjectFileRecords { get { return injectFileRecords; } set { } }

    #endregion


    #region PUBLIC

    /// <summary>
    ///
    /// </summary>
    /// <param name="configFilePath"></param>
    public void ParseConfigurationFile(string configFilePath)
    {
      if (string.IsNullOrEmpty(configFilePath))
      {
        throw new ProxyWarningException("Config file path is invalid");
      }

      if (!File.Exists(configFilePath))
      {
        throw new ProxyWarningException("Config file does not exist");
      }

      string[] configFileLines = File.ReadAllLines(configFilePath);
      injectFileRecords.Clear();

      foreach (string tmpLine in configFileLines)
      {
        try
        {
          injectFileRecords.Add(this.VerifyRecordParameters(tmpLine));
        }
        catch (ProxyWarningException pwex)
        {
          Logging.Instance.LogMessage("CONFIG", ProxyProtocol.Undefined, Loglevel.Debug, @"InjectFile.VerifyRecordParameters(EXCEPTION) : {0}", pwex.Message);
        }
        catch (ProxyErrorException peex)
        {
          Logging.Instance.LogMessage("CONFIG", ProxyProtocol.Undefined, Loglevel.Debug, @"InjectFile.VerifyRecordParameters(EXCEPTION) : {0}", peex.Message);
        }
      }
    }

    #endregion


    #region PROTECTED

    protected InjectFileConfigRecord VerifyRecordParameters(string configFileLine)
    {
      string host = string.Empty;
      string path = string.Empty;
      string replacementResource = string.Empty;
      char[] delimiter = { ':' };

      if (string.IsNullOrEmpty(configFileLine))
      {
        throw new ProxyWarningException("Configuration line is invalid");
      }

      string[] splitter = configFileLine.Split(delimiter, 3);

      if (splitter.Length != 3)
      {
        throw new ProxyWarningException("Wrong numbers of configuration parameters");
      }

      host = splitter[0]?.ToLower();
      path = splitter[1];
      replacementResource = splitter[2];

      if (string.IsNullOrEmpty(host) || !Regex.Match(host, @"[\d\w_\-\.]").Success)
      {
        throw new ProxyWarningException(string.Format("Host parameter is invalid: {0}", host));
      }

      if (string.IsNullOrEmpty(path) || Regex.Match(host, @"[\r\n\s]").Success)
      {
        throw new ProxyWarningException(string.Format("Path parameter is invalid: {0}", path));
      }

      if (string.IsNullOrEmpty(replacementResource) || Regex.Match(host, @"[\r\n\s]").Success)
      {
        throw new ProxyWarningException(string.Format("Replacement resource parameter is invalid: {0}", replacementResource));
      }

      if (injectFileRecords.Exists(elem => elem.Host == host && elem.Path == path))
      {
        throw new ProxyWarningException(string.Format("Record already exists"));
      }

      return new InjectFileConfigRecord(host, path, replacementResource);
    }

    #endregion

  }
}