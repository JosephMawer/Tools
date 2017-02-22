﻿namespace HttpReverseProxy.Plugin.InjectFile.DataTypes
{

  public class InjectFileConfigRecord
  {

    #region MEMBERS

    private string host;
    private string path;
    private string replacementResource;

    #endregion


    #region PROPERTIES

    public string Host { get { return this.host; } set { this.host = value; } }

    public string Path { get { return this.path; } set { this.path = value; } }

    public string ReplacementResource { get { return this.replacementResource; } set { this.replacementResource = value; } }

    #endregion


    #region PUBLIC

    public InjectFileConfigRecord()
    {
      this.host = string.Empty;
      this.path = string.Empty;
      this.replacementResource = string.Empty;
    }

    public InjectFileConfigRecord(string host, string path, string replacementResource)
    {
      this.host = host;
      this.path = path;
      this.replacementResource = replacementResource;
    }

    #endregion 

  }
}