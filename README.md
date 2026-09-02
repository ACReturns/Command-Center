# Command-Center

A local build tool for individuals to Update/ Patch and Launch local builds. The tool also gives the user the ability to access 
internally hosted servers to launch the game on. While I initially focused on ensuring both Development & Live Environments were 
the only projects the app could see, I ended up branching out to make it more useful and future proof by giving users the ability to 
create their own tab, set the servers they wish to utilize that are in the app by default and if not then add their own. 

Utilizing C# & WPF this tool is mainly being used for launching & testing Maplestory/ Maplestory Classic along with other projects 
down the road. 

-------

Here are some of the essential sections I organized and would like to highlight:

1. [UI](#ui)
2. [Server Functions](#functions)
3. [Profile Handler](#profile)
4. [Experience](#experience)


# UI 
With this being designed while I'm at Nexon, I themed it after their products. The overall layout of the UI elements is designed to help users easily navigate from acquiring a build to then extracting, patching & launching it all in one place. Along with that any documentation for that build that is passed along can be added to the UI and it will update. Once the version number changes prior to the delimiter is the only time it will then update the documents directory. At the bottom, there is a space notifier to alert users when they get closer to maximum capacity and it does gradient color change the lower you get. 

<img width="818" height="639" alt="Main" src="https://github.com/user-attachments/assets/5db97d90-885e-4ea9-b56e-4e31c28c8f57" />


New Builds & Patch's function almost similarly in terms of their process with the only difference in Patching being that the build isn't deleted but the changes are just appended to the existing build. Updating the Version number is optional but does help when you get multiple builds.

<img width="817" height="639" alt="Main_NewBuild" src="https://github.com/user-attachments/assets/dbbf0b0e-c7d1-4904-8fc8-7873920f94a7" />


The exclusive option I have in Live is called "Push to Live". Once all the verifications are done and the game is in the publics hands we need to keep that exact build locally somewhere incase we need to do any verifications in test. Officially we move on to a new build but keeping all the files & documentation was always an issue since there wasn't a way to organize anything. With this feature now you take all the files you have up to this point and relocate it all into a safe folder that also is labeled with the version so you don't have to guess what it is. 

<img width="818" height="639" alt="Main_Push" src="https://github.com/user-attachments/assets/9ab4eeb1-4042-41c4-bfb2-c9af1727a8a7" />


# Functions 
In Server Status I track all the current internal servers, ping to verify if they are active or not and return with a visual representation on what the outcome was. 

<img width="818" height="640" alt="Servers" src="https://github.com/user-attachments/assets/eadbb249-0c2c-4ccd-b5f7-9ded70f5da0b" />


I added an option to add your own server collection via Json files and the tool would refresh and read your file and give a visual representation of what is active/ down.

<img width="818" height="639" alt="Servers_Create" src="https://github.com/user-attachments/assets/1001f5c3-ac23-4398-a487-a3fbdaf3c2b5" />


# Profile 
In Settings we have the profile handler. Here we have the initial default profiles along with an option to create your own profile but you can also reorganize them in any order you want to reflect that in the tab order. On top of that you have the ability to adjusting which servers are available to launch on or executables to launch with per profile.

<img width="819" height="639" alt="Settings" src="https://github.com/user-attachments/assets/21831e40-cdb5-4c72-82ce-6b17b6b457c9" />


When you have a profile with some servers you will have the ability to enable/ disable any that you want. Doing so will directly influence what your options are when it comes to launching the game in this case as they require servers to run. You can adjust it as you see fit with the default or add your own.

<img width="819" height="639" alt="Settings_Servers" src="https://github.com/user-attachments/assets/09c91d46-d874-4043-bca0-154c38ef6fc3" />


With the custom server you can name it whatever you would like, add the connection type, IP & Port. Once you save it will verify if it can connect before letting you continue so you don't have a dead server in the list.

<img width="819" height="639" alt="Settings_CustomServers" src="https://github.com/user-attachments/assets/b3326e77-187f-4617-89ab-dc1f3f91632d" />


This option will only populate after you select a home folder for the profile to manage. Once you do it takes all of the top level only (no subfolder) executables and lists them out for the user to decide which they want to remain visible in the application. When they go to launch the game, they will be given the choices they chose to remain enabled. The last selection will always be remembered just to save the user time if they don't hop around too often but still need access to the other executables.

<img width="819" height="639" alt="Settings_Exes" src="https://github.com/user-attachments/assets/294b8379-10e2-4eac-b47f-6df2820205f3" />


# Experience 
Throughout the journey of developing this application, I had a small scope for what I wanted to do and primarily just make it a current in studio project tool. As I continued to develop it though I always wanted to make it more future proof and able to stand on its own without needing future interference from myself to update it because a new project was brought to the studio. Now users have the ability to do that themselves at any point without requiring a Jira ticket or extra engineering time to update the tool to incorporate them. It was a great learning experience and now it's on to the next project. 
