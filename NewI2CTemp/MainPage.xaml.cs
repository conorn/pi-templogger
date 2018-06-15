using System;
using System.Diagnostics;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Devices.I2c;
using Windows.Devices.Gpio;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net;




namespace NewI2CTemp
{
    /// <summary>
    /// Sample app that reads data over I2C from an attached ADXL345 accelerometer
    /// </summary>
    public sealed partial class MainPage : Page
    {
        /* Important! Set the correct I2C controller name for your target device here */
        private const string I2C_CONTROLLER_NAME = "I2C1";        /* For Raspberry Pi 2, use I2C1 */

        private const byte TEMP_I2C_ADDR = 0x48;            /* 7-bit I2C address of the TMP102, ADDR= Gnd.      */
        private const byte CONFIG_REG_ADDR = 0x01;          /* Address of the Contfig register */
        private const byte TEMP_REG_ADDR = 0x00;            /* Address of the Data Format register   */
        private I2cDevice I2CTemp;                          // the device object

        //for Adafruit IO access
        private String AIO_Base_Uri = "https://io.adafruit.com/api/groups/RPi/send.json?x-aio-key="; 
        private String AIO_Key = "5aff637cfca9b73b505dca2652662f40ce48b1e0"; 


        private DispatcherTimer periodicTimer_AIO;

        //for LED GPIO
        private const int LED_PIN = 5;
        private GpioPin pin;

        private const int BIG_DELAY = 60000;    //one minute
        private const int SMALL_DELAY = 100;    //100mS
     

        public MainPage()
        {
            this.InitializeComponent();

            /* Register for the unloaded event so we can clean up upon exit */
            Unloaded += MainPage_Unloaded;

            // Init the LED's GPIO controller
            InitGPIO();

            /* Initialize the I2C bus and timer */
            InitI2CTemp();
        }

        private async void InitI2CTemp()
        {
            //Let AIO know we are starting up (sceen status is already set
            Text_Status.Text = "Status: Initialising ...";

            //As this is first access to AIO should do try/catch.  Why have to use Async?
            //using WebRequest - hhtpClient elsewhere
            String req = AIO_Base_Uri + AIO_Key + "&status=" + "Initialising";
            HttpWebRequest g = (HttpWebRequest)WebRequest.Create(req);
            var r = g.GetResponseAsync();

            /* Initialize the I2C bus */
            try
            {
                var settings = new I2cConnectionSettings(TEMP_I2C_ADDR);
                settings.BusSpeed = I2cBusSpeed.FastMode;

                string aqs = I2cDevice.GetDeviceSelector(I2C_CONTROLLER_NAME);  /* Find the selector string for the I2C bus controller                   */
                var dis = await DeviceInformation.FindAllAsync(aqs);            /* Find the I2C bus controller device with our selector string           */
                I2CTemp = await I2cDevice.FromIdAsync(dis[0].Id, settings);    /* Create an I2cDevice with our selected bus controller and I2C settings */

            }
            /* If initialization fails, display the exception and stop running */
            catch (Exception e)
            {
                Text_Status.Text = "Exception: " + e.Message;
                return;
            }



            /* Write the register settings */
            byte[] WriteBufConf = new byte[] { CONFIG_REG_ADDR, 0x61, 0xA0 };  //60/A0 is normal, 61 is SD (shutdown, for one-shot)
            //byte[] WriteBufData = new byte[] { TEMP_REG_ADDR };
            try
            {
                I2CTemp.Write(WriteBufConf); //default conditions for config reg
                //I2CTemp.Write(WriteBufData); //set to temp reg
            }
            /* If the write fails display the error and stop running */
            catch (Exception)
            {
                Text_Status.Text = "Status: Initialization failed";
                return;
            }

            /* Now that everything is initialized, create a timer so we read data every minute (was 500mS) */
            periodicTimer_AIO = new DispatcherTimer();
            periodicTimer_AIO.Interval = TimeSpan.FromMilliseconds(BIG_DELAY);
            periodicTimer_AIO.Tick += Timer_Tick;
            periodicTimer_AIO.Start();

            Debug.WriteLine("Init complete");

            //Double-Blip the LED so we know init is done.
            FlashLED();
            await WaitaBit();   // needed a load of these ???
            await WaitaBit();
            await WaitaBit();
            await WaitaBit();
            FlashLED();

        }

        private void MainPage_Unloaded(object sender, object args)
        {
            /* Cleanup */
            I2CTemp.Dispose();

            //!!doesn't work!!
            String req = AIO_Base_Uri + AIO_Key + "&status=" + "Finished";
            HttpWebRequest g = (HttpWebRequest)WebRequest.Create(req);
            var r = g.GetResponseAsync();
        }

        //the meat - called by event handler - read sensor, format data and update screen and AIO
        private async void ReadI2CTemp()
        {

            byte TempRawMSB, TempRawLSB;  //use later

            byte[] ReadBuf = new byte[2]; // MSB first, then LSB 
            byte[] WriteBuf = new byte[] { TEMP_REG_ADDR };       // 0 selects data register - write it out first
            byte[] ConfBuf = new byte[] { CONFIG_REG_ADDR, 0xE1, 0xA0 };        //hit the OS bit (assume rest unchanged)
                                                                                //should read and mask bit ...
                                                                                //byte[] WriteBuf = new byte[] { CONFIG_REG_ADDR, 0xE1, 0xA0 };
            try
            {
                /* 
                 * Read from the temp sensor 
                 */
                I2CTemp.Write(ConfBuf);
                I2CTemp.WriteRead(WriteBuf, ReadBuf);
                //I2CTemp.Read(ReadBuf);

            }
            catch (Exception e)
            {
                /* If WriteRead() fails, display error messages */
                Text_X_Axis.Text = "Error";
                Text_Status.Text = "Exception: " + e.Message;
                return;
            }

            /* 
             * In order to get the raw 16-bit data values, we need to concatenate two 8-bit bytes from the I2C read for each axis.
             * We accomplish this by using bit shift and logical OR operations
             */
            //TempRaw = (Int16)(ReadBuf[0] | ReadBuf[1] << 8);
            TempRawMSB = ReadBuf[0];
            TempRawLSB = ReadBuf[1];


            /* Convert raw values to G's */
            //AccelerationX = (double)AccelerationRawX / UNITS_PER_G;
            int temp = ((TempRawMSB << 8) | TempRawLSB) >> 4;
            //double celsius = temp * 0.0625;
            String Celsius = (temp * 0.0625).ToString("F2");

            /* Display the values */
            Text_X_Axis.Text = "Temp (RAW): " + TempRawMSB.ToString("X2") + " " + TempRawLSB.ToString("X2");
            Text_Y_Axis.Text = "Celsius: " + Celsius;
            Text_Status.Text = "Status: Running";

            //Adafruit IO Web update
            //Call function to update AIO with temp data.  A simple GET.  Returns status code
            String resCode = await UpdateAIO(Celsius);

            //Report the status on screen/console.  Should be 200/OK
            AIO_Status.Text = "AIO Status: " + resCode;

            //Blip the LED so we know something happening.
            FlashLED();

        }


        // the event handler - read sensor and update displays
        private void Timer_Tick(object sender, object e)
        {
            // Debug.WriteLine("in long (AIO) timer");
            ReadI2CTemp(); /* Read data from the I2C device and display it on screen and AIO */
        }

        // update AIO with temp and time
        private async Task<String> UpdateAIO(String newtemp)
        {
            String now = DateTime.Now.ToString("H:mm");
            String req = AIO_Base_Uri + AIO_Key + "&temp=" + newtemp + "&status=" + "Running (" + now + ")";

            // HttpResponseMessage responseMsg;
            String retCode = "";

            try
            {
                HttpClient client = new HttpClient();

                // Make the call
                HttpResponseMessage responseMsg = await client.GetAsync(req);

                // Let us know what the returned string was
                //Debug.WriteLine(String.Format("Response string: [{0}]", responseString));

                retCode = responseMsg.StatusCode.ToString();

            }
            catch (Exception e)
            {
                AIO_Status.Text = "Exception: " + e.Message;
                return e.Message;

                // Debug.WriteLine(e.Message);
            }

            return retCode;

        }

        // prepare GPIO for LED
        private void InitGPIO()
        {
            var gpio = GpioController.GetDefault();

            // Show an error if there is no GPIO controller
            if (gpio == null)
            {
                pin = null;
                //GpioStatus.Text = "There is no GPIO controller on this device.";
                return;
            }

            pin = gpio.OpenPin(LED_PIN);

            // Show an error if the pin wasn't initialized properly
            if (pin == null)
            {
                //GpioStatus.Text = "There were problems initializing the GPIO pin.";
                return;
            }

            pin.Write(GpioPinValue.High);
            pin.SetDriveMode(GpioPinDriveMode.Output);

            //GpioStatus.Text = "GPIO pin initialized correctly.";
        }

        // simple short delay
        async System.Threading.Tasks.Task WaitaBit()
        {
            await System.Threading.Tasks.Task.Delay(SMALL_DELAY);
        }

        // toggles LED using WaitaBit()
        private async void FlashLED()
        {
            if (pin != null)
            {
                // to turn on the LED, we need to push the pin 'low'
                pin.Write(GpioPinValue.Low);
            }

            await WaitaBit();

            if (pin != null)
            {
                pin.Write(GpioPinValue.High);
            }
        }



        private void Title_SelectionChanged(object sender, RoutedEventArgs e)
        {

        }
    }
}
