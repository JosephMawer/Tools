﻿namespace HttpReverseProxy.Plugin.InjectCode
{
  using HttpReverseProxyLib.DataTypes;
  using HttpReverseProxyLib.DataTypes.Class;
  using HttpReverseProxyLib.DataTypes.Enum;
  using HttpReverseProxyLib.Exceptions;


  public partial class InjectCode
  {

    /// <summary>
    /// 
    /// </summary>
    /// <param name="requestObj"></param>
    /// <returns></returns>
    public PluginInstruction OnPostServerHeadersResponse(RequestObj requestObj)
    {
      PluginInstruction instruction = new PluginInstruction() { Instruction = Instruction.DoNothing };

      if (requestObj == null)
      {
        throw new ProxyWarningException("The request object is invalid");
      }

      return instruction;
    }
  }
}
