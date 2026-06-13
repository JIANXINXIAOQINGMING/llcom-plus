using System;
using System.Collections.Generic;
using System.Net;

namespace llcom_plus.HttpTools
{
    public enum RequestBodyType
    {
        Raw,
        FormUrlEncoded,
        MultipartFormData
    }

    public class FormFieldModel
    {
        public bool IsEnabled { get; set; } = true;
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class FileFieldModel
    {
        public bool IsEnabled { get; set; } = true;
        public string FieldName { get; set; } = "file";
        public string FilePath { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/octet-stream";
    }

    public class HeaderPreset
    {
        public HeaderPreset(string name, string defaultValue)
        {
            Name = name;
            DefaultValue = defaultValue;
        }

        public string Name { get; private set; }
        public string DefaultValue { get; private set; }

        public override string ToString()
        {
            return Name;
        }
    }

    public class HttpRequestModel
    {
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public string Body { get; set; } = string.Empty;
        public RequestBodyType BodyType { get; set; } = RequestBodyType.Raw;
        public List<FormFieldModel> FormFields { get; set; } = new List<FormFieldModel>();
        public List<FileFieldModel> Files { get; set; } = new List<FileFieldModel>();
    }

    public class HttpResponseModel
    {
        public HttpStatusCode StatusCode { get; set; }
        public string ReasonPhrase { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string Headers { get; set; } = string.Empty;
        public TimeSpan ElapsedTime { get; set; }
        public string RequestMethod { get; set; } = string.Empty;
        public string RequestUrl { get; set; } = string.Empty;
    }
}
