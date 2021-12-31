using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace Chat.Web.Models
{
    public class Message
    {
        public int Id { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        [ForeignKey("FromUserId")]
        [InverseProperty("Messages")]
        public ApplicationUser FromUser { get; set; }
        [ForeignKey("ToUserId")]
        [InverseProperty("ToMessages")]
        public ApplicationUser ToUser { get; set; }
        public int ToRoomId { get; set; }
        public Room ToRoom { get; set; }
    }
}
