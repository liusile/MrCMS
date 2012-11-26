using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Mvc;
using MrCMS.Entities.Documents.Web;
using MrCMS.Entities.Messaging;
using MrCMS.Helpers;
using MrCMS.Settings;
using MrCMS.Tasks;
using NHibernate;

namespace MrCMS.Services
{
    public interface IFormService
    {
        string GetFormStructure(int id);
        void SaveFormStructure(int id, string data);
        void SaveFormData(int id, FormCollection formCollection);
    }
    public class FormService : IFormService
    {
        private readonly ISession _session;
        private readonly IDocumentService _documentService;
        private readonly SiteSettings _siteSettings;
        private readonly MailSettings _mailSettings;

        public FormService(ISession session, IDocumentService documentService, SiteSettings siteSettings, MailSettings mailSettings)
        {
            _session = session;
            _documentService = documentService;
            _siteSettings = siteSettings;
            _mailSettings = mailSettings;
        }

        public string GetFormStructure(int id)
        {
            var document = _documentService.GetDocument<Webpage>(id);
            return document == null
                       ? Newtonsoft.Json.JsonConvert.SerializeObject(new object())
                       : document.FormData ?? Newtonsoft.Json.JsonConvert.SerializeObject(new object());
        }

        public void SaveFormStructure(int id, string data)
        {
            var document = _documentService.GetDocument<Webpage>(id);
            if (document == null) return;
            document.FormData = data;
            _documentService.SaveDocument(document);
        }

        public void SaveFormData(int id, FormCollection formCollection)
        {
            _session.Transact(session =>
                                                   {
                                                       var webpage = _documentService.GetDocument<Webpage>(id);
                                                       if (webpage == null) return;
                                                       var formPosting = new FormPosting
                                                                             {
                                                                                 Webpage = webpage,
                                                                                 FormValues = new List<FormValue>()
                                                                             };
                                                       formCollection.AllKeys.ForEach(s =>
                                                                                          {
                                                                                              var formValue = new FormValue
                                                                                                                  {
                                                                                                                      Key = s,
                                                                                                                      Value = formCollection[s],
                                                                                                                      FormPosting = formPosting,
                                                                                                                  };
                                                                                              formPosting.FormValues.Add(formValue);
                                                                                              session.SaveOrUpdate(formValue);
                                                                                          });

                                                       webpage.FormPostings.Add(formPosting);
                                                       session.SaveOrUpdate(formPosting);

                                                       SendFormMessages(webpage, formPosting);
                                                   });
        }

        private void SendFormMessages(Webpage webpage, FormPosting formPosting)
        {
            var sendTo = webpage.SendFormTo.Split(',');
            if (sendTo.Any())
            {
                _session.Transact(session =>
                                                       {
                                                           foreach (var email in sendTo)
                                                           {
                                                               var formMessage = ParseFormMessage(webpage.FormMessage, webpage,
                                                                                                  formPosting);
                                                               var formTitle = ParseFormMessage(webpage.FormEmailTitle, webpage,
                                                                                                formPosting);

                                                               session.SaveOrUpdate(new QueuedMessage
                                                                                        {
                                                                                            Subject = formTitle,
                                                                                            Body = formMessage,
                                                                                            FromAddress = _siteSettings.SystemEmailAddress,
                                                                                            ToAddress = email,
                                                                                            IsHtml = true
                                                                                        });
                                                           }

                                                           TaskExecutor.ExecuteLater(new SendQueuedMessagesTask(_mailSettings));
                                                       });
            }
        }

        private static string ParseFormMessage(string formMessage, Webpage webpage, FormPosting formPosting)
        {

            var formRegex = new Regex(@"\[form\]");
            var pageRegex = new Regex(@"{{page.(.*)}}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var messageRegex = new Regex(@"{{(.*)}}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            formMessage = formRegex.Replace(formMessage, match =>
                                                             {
                                                                 var list = new TagBuilder("ul");

                                                                 foreach (var formValue in formPosting.FormValues)
                                                                 {
                                                                     var listItem = new TagBuilder("li");

                                                                     var title = new TagBuilder("b");
                                                                     title.InnerHtml += formValue.Key + ":";
                                                                     listItem.InnerHtml += title.ToString() + " " +
                                                                                           formValue.Value;

                                                                     list.InnerHtml += listItem.ToString();
                                                                 }

                                                                 return list.ToString();
                                                             });

            formMessage = pageRegex.Replace(formMessage, match =>
                                                             {
                                                                 var propertyInfo =
                                                                     typeof(Webpage).GetProperties().FirstOrDefault(
                                                                         info =>
                                                                         info.Name.Equals(match.Value.Replace("{", "").Replace("}", "").Replace("page.", ""),
                                                                                          StringComparison.OrdinalIgnoreCase));

                                                                 return propertyInfo == null
                                                                            ? string.Empty
                                                                            : propertyInfo.GetValue(webpage,
                                                                                                    null).
                                                                                           ToString();
                                                             });
            return messageRegex.Replace(formMessage, match =>
                                                         {
                                                             var formValue =
                                                                 formPosting.FormValues.FirstOrDefault(
                                                                     value =>
                                                                     value.Key.Equals(
                                                                         match.Value.Replace("{", "").Replace("}", ""),
                                                                         StringComparison.
                                                                             OrdinalIgnoreCase));
                                                             return formValue == null
                                                                        ? string.Empty
                                                                        : formValue.Value;
                                                         });
        }
    }
}