﻿using AutoMapper;
using Chat.Web.Data;
using Chat.Web.Models;
using Chat.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Chat.Web.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        public readonly static List<UserViewModel> _Connections = new List<UserViewModel>();
        public List<UserViewModel> _ChatUsers = new List<UserViewModel>();
        public readonly static List<RoomViewModel> _Rooms = new List<RoomViewModel>();
        private readonly static Dictionary<string, string> _ConnectionsMap = new Dictionary<string, string>();

        private readonly ApplicationDbContext _context;
        private readonly IMapper _mapper;

        public ChatHub(ApplicationDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }



        public async Task SendPrivate(string receiverName, string message)
        {
            if (_ConnectionsMap.TryGetValue(receiverName, out string userId))
            {
                // Who is the sender;
                var sender = _Connections.Where(u => u.Username == IdentityName).First();
                var user = _context.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();
                var toUser = _context.Users.Where(u => u.UserName == receiverName).FirstOrDefault();                
                if (!string.IsNullOrEmpty(message.Trim()))
                {
                    // Build the message
                    var messageViewModel = new MessageViewModel()
                    {
                        Content = Regex.Replace(message, @"(?i)<(?!img|a|/a|/img).*?>", string.Empty),
                        From = sender.FullName,
                        Avatar = sender.Avatar,
                        To = receiverName,
                        FromUserName = sender.Username,
                        ToUserName = receiverName,
                        Timestamp = DateTime.Now.ToLongTimeString()
                    };

                    var msg = new Message()
                    {
                        Content = Regex.Replace(message, @"(?i)<(?!img|a|/a|/img).*?>", string.Empty),
                        FromUser = user,
                        ToUser = toUser,
                        ToRoom = null,
                        Timestamp = DateTime.Now
                    };
                    _context.Messages.Add(msg);
                    _context.SaveChanges();

                    var fromUser = GetUserByName(sender.Username);

                    // Send the message
                    await Clients.Client(userId).SendAsync("newMessage", messageViewModel, fromUser);
                    await Clients.Caller.SendAsync("newMessage", messageViewModel, fromUser);
                }
            }
        }

        public async Task SendToRoom(string roomName, string message)
        {
            try
            {
                var user = _context.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();
                var room = _context.Rooms.Where(r => r.Name == roomName).FirstOrDefault();

                if (!string.IsNullOrEmpty(message.Trim()))
                {
                    // Create and save message in database
                    var msg = new Message()
                    {
                        Content = Regex.Replace(message, @"(?i)<(?!img|a|/a|/img).*?>", string.Empty),
                        FromUser = user,
                        ToRoom = room,
                        Timestamp = DateTime.Now
                    };
                    _context.Messages.Add(msg);
                    _context.SaveChanges();

                    // Broadcast the message
                    var messageViewModel = _mapper.Map<Message, MessageViewModel>(msg);
                    await Clients.Group(roomName).SendAsync("newMessage", messageViewModel, null);
                }
            }
            catch (Exception)
            {
                await Clients.Caller.SendAsync("onError", "Message not send! Message should be 1-500 characters.");
            }
        }

        public async Task Join(string roomName)
        {
            try
            {
                var user = _Connections.Where(u => u.Username == IdentityName).FirstOrDefault();
                if (user != null && user.CurrentRoom != roomName)
                {
                    // Remove user from others list
                    if (!string.IsNullOrEmpty(user.CurrentRoom))
                        await Clients.OthersInGroup(user.CurrentRoom).SendAsync("removeUser", user);

                    // Join to new chat room
                    await Leave(user.CurrentRoom);
                    await Groups.AddToGroupAsync(Context.ConnectionId, roomName);
                    user.CurrentRoom = roomName;

                    // Tell others to update their list of users
                    await Clients.OthersInGroup(roomName).SendAsync("addUser", user);
                }
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("onError", "You failed to join the chat room!" + ex.Message);
            }
        }

        public async Task Leave(string roomName)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomName);
        }

        public async Task CreateRoom(string roomName)
        {
            try
            {

                // Accept: Letters, numbers and one space between words.
                Match match = Regex.Match(roomName, @"^\w+( \w+)*$");
                if (!match.Success)
                {
                    await Clients.Caller.SendAsync("onError", "Invalid room name!\nRoom name must contain only letters and numbers.");
                }
                else if (roomName.Length < 5 || roomName.Length > 100)
                {
                    await Clients.Caller.SendAsync("onError", "Room name must be between 5-100 characters!");
                }
                else if (_context.Rooms.Any(r => r.Name == roomName))
                {
                    await Clients.Caller.SendAsync("onError", "Another chat room with this name exists");
                }
                else
                {
                    // Create and save chat room in database
                    var user = _context.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();
                    var room = new Room()
                    {
                        Name = roomName,
                        Admin = user
                    };
                    _context.Rooms.Add(room);
                    _context.SaveChanges();

                    if (room != null)
                    {
                        // Update room list
                        var roomViewModel = _mapper.Map<Room, RoomViewModel>(room);
                        _Rooms.Add(roomViewModel);
                        await Clients.All.SendAsync("addChatRoom", roomViewModel);
                    }
                }
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("onError", "Couldn't create chat room: " + ex.Message);
            }
        }

        public async Task DeleteRoom(string roomName)
        {
            try
            {
                // Delete from database
                var room = _context.Rooms.Include(r => r.Admin)
                    .Where(r => r.Name == roomName && r.Admin.UserName == IdentityName).FirstOrDefault();
                _context.Rooms.Remove(room);
                _context.SaveChanges();

                // Delete from list
                var roomViewModel = _Rooms.First(r => r.Name == roomName);
                _Rooms.Remove(roomViewModel);

                // Move users back to Lobby
                await Clients.Group(roomName).SendAsync("onRoomDeleted", string.Format("Room {0} has been deleted.\nYou are now moved to the Lobby!", roomName));

                // Tell all users to update their room list
                await Clients.All.SendAsync("removeChatRoom", roomViewModel);
            }
            catch (Exception)
            {
                await Clients.Caller.SendAsync("onError", "Can't delete this chat room. Only owner can delete this room.");
            }
        }

        public IEnumerable<RoomViewModel> GetRooms()
        {
            // First run?
            if (_Rooms.Count == 0)
            {
                foreach (var room in _context.Rooms)
                {
                    var roomViewModel = _mapper.Map<Room, RoomViewModel>(room);
                    _Rooms.Add(roomViewModel);
                }
            }

            return _Rooms.ToList();
        }

        public IEnumerable<UserViewModel> GetUsers(string roomName)
        {
            return _Connections.Where(c=> c.Username != IdentityName).ToList();
        }
        public IEnumerable<UserViewModel> GetChatUsers()
        {

            var currentUser = _Connections.Where(u => u.Username == IdentityName).FirstOrDefault();            
            var messages = _context.Messages.Include(c => c.FromUser).Include(c => c.ToUser)
                .Where(c => c.FromUser.UserName == currentUser.Username || (c.ToUser !=null && c.ToUser.UserName == currentUser.Username));
            foreach (var msgs in messages)
            {                
                if (msgs.FromUser != null && !_ChatUsers.Any(c => c?.Username == msgs.FromUser.UserName) && msgs.FromUser.UserName != currentUser.Username)
                    _ChatUsers.Add(_mapper.Map<ApplicationUser, UserViewModel>(msgs.FromUser));
                if (msgs.ToUser != null && !_ChatUsers.Any(c => c?.Username == msgs?.ToUser?.UserName) && msgs?.ToUser?.UserName != currentUser.Username)
                    _ChatUsers.Add(_mapper.Map<ApplicationUser, UserViewModel>(msgs.ToUser));
            }

            return _ChatUsers;
        }

        public UserViewModel GetUserByName(string userName)
        {
            var user = _context.AppUsers.SingleOrDefault(c => c.UserName == userName);

            return _mapper.Map<ApplicationUser, UserViewModel>(user);
        }

        public IEnumerable<MessageViewModel> GetChatHistory(string userName, string receiverName)
        {
            var messageHistory = _context.Messages.Where(m=> 
            ((m.ToUser != null && m.ToUser.UserName == userName) && (m.FromUser!=null &&m.FromUser.UserName == receiverName) ||
            (m.ToUser != null && m.ToUser.UserName == receiverName) && (m.FromUser != null && m.FromUser.UserName == userName)
            )
            )
                    .Include(m => m.FromUser)
                    .Include(m => m.ToRoom)
                    .OrderByDescending(m => m.Timestamp)
                    .Take(20)
                    .AsEnumerable()
                    .Reverse()
                    .ToList();

            return _mapper.Map<IEnumerable<Message>, IEnumerable<MessageViewModel>>(messageHistory);
        }
        public IEnumerable<MessageViewModel> GetMessageHistory(string roomName)
        {
            var messageHistory = _context.Messages.Where(m => m.ToRoom.Name == roomName)
                    .Include(m => m.FromUser)
                    .Include(m => m.ToRoom)
                    .OrderByDescending(m => m.Timestamp)
                    .Take(20)
                    .AsEnumerable()
                    .Reverse()
                    .ToList();

            return _mapper.Map<IEnumerable<Message>, IEnumerable<MessageViewModel>>(messageHistory);
        }

        public override Task OnConnectedAsync()
        {
            try
            {
                var user = _context.Users.Where(u => u.UserName == IdentityName).FirstOrDefault();
                var userViewModel = _mapper.Map<ApplicationUser, UserViewModel>(user);
                userViewModel.Device = GetDevice();
                userViewModel.CurrentRoom = "";

                if (!_Connections.Any(u => u.Username == IdentityName))
                {
                    _Connections.Add(userViewModel);
                    _ConnectionsMap.Add(IdentityName, Context.ConnectionId);
                }

                Clients.Caller.SendAsync("getProfileInfo", user.UserName,user.FullName, user.Avatar);

                Clients.AllExcept(IdentityName).SendAsync("addUserOnConnect", user.UserName, user.FullName, user.Avatar, userViewModel.Device);
               
            }
            catch (Exception ex)
            {
                Clients.Caller.SendAsync("onError", "OnConnected:" + ex.Message);
            }
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            try
            {
                var user = _Connections.Where(u => u.Username == IdentityName).First();
                _Connections.Remove(user);

                // Tell other users to remove you from their list
                Clients.OthersInGroup(user.CurrentRoom).SendAsync("removeUser", user);

                // Remove mapping
                _ConnectionsMap.Remove(user.Username);

                Clients.AllExcept(IdentityName).SendAsync("removeUserOnDisconnectConnect", user.Username);
                
            }
            catch (Exception ex)
            {
                Clients.Caller.SendAsync("onError", "OnDisconnected: " + ex.Message);
            }

            return base.OnDisconnectedAsync(exception);
        }

        private string IdentityName
        {
            get { return Context.User.Identity.Name; }
        }

        private string GetDevice()
        {
            var device = Context.GetHttpContext().Request.Headers["Device"].ToString();
            if (!string.IsNullOrEmpty(device) && (device.Equals("Desktop") || device.Equals("Mobile")))
                return device;

            return "Web";
        }
    }
}
