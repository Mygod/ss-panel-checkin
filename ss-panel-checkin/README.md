# ss-panel-checkin
As we all know, the checkin feature in ss-panels is just a royal pain in the ass. This program is designed to help out those lazy ass people including me.

To use this program, create a file named `config.xml` under the same directory as the executive, and then add lines following the syntax given in `config-example.xml`. After that, just double click on `ss-panel-checkin.exe` and everything will just work fine. If not, please check the log and try to resolve the problem. If you found any bugs/improvements, please open an issue or even better, submit a PR. :-D

## Custom configuration path
The program use `config.xml` under its current working directory. However, you can supply your custom config file path as its first command line argument to use the file.

## Some sites are not being checked in on time or at all?
As soon as one triggers a fatal error, it will be removed from the queue to reduce system resources usage. When a normal error occurs several times in a row, the retry timeout will increase exponentially from 1 seconds up to 17 minutes (1024s) to reduce system resources usage even more.

If you want to skip the wait, press `A` to make the program will start processing again. (however the fail attempts counter will never reset unless everything is going well or the program is closed)

If you want to retry the sites that have been removed from the queue, press `R` to reload the config to add them back again.

If you think some exceptions shouldn't be considered as a fatal error, please also submit an issue/PR. Thanks.
