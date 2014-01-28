//------------------------------------------------------------------------------
// <auto-generated>
//    This code was generated from a template.
//
//    Manual changes to this file may cause unexpected behavior in your application.
//    Manual changes to this file will be overwritten if the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace OCM.Core.Data
{
    using System;
    using System.Collections.Generic;
    
    public partial class RegisteredApplication
    {
        public int ID { get; set; }
        public string Title { get; set; }
        public string WebsiteURL { get; set; }
        public string Description { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsWriteEnabled { get; set; }
        public string PrimaryAPIKey { get; set; }
        public string DeprecatedAPIKey { get; set; }
        public string SharedSecret { get; set; }
        public Nullable<System.DateTime> DateAPIKeyLastUsed { get; set; }
        public System.DateTime DateCreated { get; set; }
        public int UserID { get; set; }
    
        public virtual User User { get; set; }
    }
}