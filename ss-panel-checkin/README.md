# ss-panel-checkin
As we all know, the checkin feature in ss-panels is just a royal pain in the ass. This program is designed to help out those lazy ass people including me.

To use this program, create a file named `config.csv` under the same directory as the executive, and then add lines following the syntax:

	http://www.myproxy.com:8080
	Site name,[proxy|],http://example.com,1,b598e776@opayq.com,,my_lame_encrypted_password
	Add more sites here

You can view your uid, email, user name and encrypted password in the cookies. You should leave email or user name blank if the cookie doesn't provide the information to make the program work properly.

After that, just double click on `ss-panel-checkin.exe` and everything will just work fine. If not, please check the log and try to resolve the problem. If you found any bugs/improvements, please open an issue or even better, submit a PR. :-D

## Custom configuration path
The program use `config.csv` under its current working directory. However, you can supply your custom csv file path as its first command line argument to use the file.

## Some sites are not being checked in on time or at all?
As soon as one triggers a fatal error, it will be removed from the queue to reduce system resources usage. When a normal error occurs several times in a row, the retry timeout will increase exponentially from 1 seconds up to 17 minutes (1024s) to reduce system resources usage even more.

If you don't want this happen, press `R` to reload the config and the program will reset the queue and start processing again. (however the fail attempts counter will never reset unless everything is going well or the program is closed)

If you think some exceptions shouldn't be considered as a fatal error, please also submit an issue/PR. Thanks.
